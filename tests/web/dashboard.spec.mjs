import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { readFile } from 'node:fs/promises';
import { installServer, lastfmToken, passwordLogin, token } from './fixture.mjs';

for (const prefix of ['', '/jellyfin']) {
  test(`password access, safe discovery and private playlists at ${prefix || 'root'}`, async ({ page }) => {
    const state = await installServer(page, { prefix });
    await passwordLogin(page);
    await expect(page.locator('#password')).toHaveValue('');
    await expect(page.locator('#administrator')).toBeHidden();
    await expect(page.locator('#jellyfin-link')).toHaveAttribute('href', `${prefix}/web/index.html`);
    await page.getByRole('button', { name: 'Discover music', exact: true }).click();
    await expect(page.locator('#discovery-external')).toContainText('<img src=x onerror=alert(1)>');
    await expect(page.locator('#discovery-external img')).toHaveCount(0);
    await expect(page.locator('#discovery-external a')).toHaveCount(0);
    await expect(page.locator('#discovery-artists')).toContainText('Portishead');
    await expect(page.locator('#discovery-albums')).toContainText('Dummy');
    await page.getByLabel('Playlist name').fill('Evening listening');
    await page.getByRole('button', { name: 'Create private playlist' }).click();
    await expect(page.locator('#playlist-list')).toContainText('Evening listening');
    await page.getByRole('button', { name: 'Stop managing', exact: true }).click();
    await expect(page.locator('#status')).toContainText('playlist was kept');
    expect(state.requests.every(request => !request.url.includes(token))).toBe(true);
    const me = state.requests.find(request => request.path === '/JellySin/Lastfm/Me');
    expect(me.headers.authorization).toContain(`Token="${token}"`);
    const playlist = state.requests.find(request => request.path === '/JellySin/Lastfm/Me/Playlists' && request.method === 'POST');
    expect(playlist.body.userId).toBeUndefined();
    expect(playlist.body.playlistId).toBeUndefined();
    await page.getByRole('button', { name: 'Sign out', exact: true }).click();
    await expect(page.locator('#sign-in')).toBeVisible();
    await expect(page.locator('#discovery-external')).toBeEmpty();
    await expect(page.locator('#playlist-list')).toBeEmpty();
    expect(state.disconnected).toBe(false);
  });
}

test('Quick Connect exchanges secrets only in request bodies', async ({ page }) => {
  const state = await installServer(page, { prefix: '/jellyfin' });
  await page.getByRole('button', { name: 'Use Quick Connect' }).click();
  await expect(page.locator('#quick-code')).toHaveText('123456');
  await expect(page.locator('#signed-in')).toBeVisible({ timeout: 10_000 });
  const checks = state.requests.filter(request => request.path.endsWith('QuickConnect/Status'));
  expect(checks).toHaveLength(1);
  expect(checks[0].body.secret).toBe('q'.repeat(32));
  expect(state.requests.every(request => !request.url.includes('q'.repeat(32)))).toBe(true);
});

test('password login cancels a pending Quick Connect attempt', async ({ page }) => {
  const state = await installServer(page);
  await page.clock.install();
  await page.getByRole('button', { name: 'Use Quick Connect' }).click();
  await expect(page.locator('#quick-code')).toHaveText('123456');
  await passwordLogin(page);
  await page.clock.fastForward(15_000);
  await expect(page.locator('#quick-instructions')).toBeHidden();
  expect(state.requests.filter(request => request.path.endsWith('AuthenticateWithQuickConnect'))).toHaveLength(0);
  expect(state.requests.filter(request => request.path.endsWith('QuickConnect/Status'))).toHaveLength(0);
});

test('history changes are paged, resumable and only applied after confirmation', async ({ page }) => {
  const state = await installServer(page);
  await passwordLogin(page);
  await page.getByText('Import play counts into Jellyfin', { exact: true }).click();
  await page.getByRole('button', { name: 'Prepare preview', exact: true }).click();
  await expect(page.locator('#history-preview-list')).toContainText('201 matched tracks');
  await expect(page.locator('#history-apply')).toBeDisabled();
  await expect(page.locator('#history-preview-list')).toContainText('Last-played dates: through history page 0');
  expect(state.imported).toBe(false);
  await page.getByRole('button', { name: 'Next changes' }).click();
  await expect(page.locator('#history-preview-list')).toContainText('Angel');
  await page.getByRole('button', { name: 'Resume saved preview' }).click();
  await expect(page.locator('#preview-page')).toHaveText('Preview page 1 of 2');
  expect(state.imported).toBe(false);
  await page.getByRole('button', { name: 'Continue preview', exact: true }).click();
  await expect(page.locator('#history-continue')).toBeHidden();
  await page.getByRole('button', { name: 'Confirm and import preview' }).click();
  await expect(page.locator('#history-preview-list')).toContainText('200 of 201 tracks already applied');
  expect(state.imported).toBe(false);
  await page.reload();
  await expect(page.locator('#signed-in')).toBeVisible();
  await page.getByText('Import play counts into Jellyfin', { exact: true }).click();
  await page.getByRole('button', { name: 'Resume saved preview' }).click();
  await page.getByRole('button', { name: 'Continue confirmed import', exact: true }).click();
  await expect(page.locator('#history-apply')).toBeDisabled();
  expect(state.imported).toBe(true);
  expect(state.requests.filter(request => request.path.endsWith('History/Import'))).toHaveLength(2);
});

test('favourite removals and disconnect need explicit review', async ({ page }) => {
  const state = await installServer(page);
  await passwordLogin(page);
  await page.getByRole('button', { name: 'Refresh review' }).click();
  await expect(page.locator('#removal-list')).toContainText('Remove the favourite from Last.fm?');
  expect(state.removed).toBe(false);
  await page.getByRole('button', { name: 'Keep unchanged' }).click();
  await expect(page.locator('#removal-list')).toContainText('No removals');
  const review = state.requests.find(request => request.path.endsWith('Favourites/Review'));
  expect(review.body.apply).toBe(false);
  await page.getByRole('button', { name: 'Disconnect', exact: true }).click();
  expect(state.disconnected).toBe(false);
  await page.getByRole('button', { name: 'Disconnect and clear plugin data' }).click();
  await expect(page.locator('#connection-status')).toContainText('Connect Last.fm to start');
  expect(state.disconnected).toBe(true);
});

test('favourite synchronization reports running server state', async ({ page }) => {
  const state = await installServer(page);
  state.favouritesRunning = true;
  await passwordLogin(page);
  await page.getByRole('button', { name: 'Refresh review' }).click();
  await expect(page.locator('#favourites-status')).toHaveText('Synchronization is running on the server. Select Refresh review to see the result.');
  await page.getByRole('button', { name: 'Sync now' }).click();
  await expect(page.locator('#status')).toHaveText('Synchronization continues on the server.');
});

// What the page offers only with a connected Last.fm account. The list is written out: an element that loses its mark fails here.
const sections = ['overview', 'history', 'favourites', 'discovery', 'playlists'];
const needsConnection = [
  ...sections.map(id => `#${id}`), ...sections.map(id => `nav.section-nav a[href="#${id}"]`),
  '#disconnect-lastfm', 'label:has(#scrobbling)', '#library-exclusions', '#device-exclusions', '#media-type-exclusions', '#scrobble-log', 'details:has(#delivery-status)',
];

async function expectOffered(page, offered) {
  await expect(page.locator('[data-needs-connection]')).toHaveCount(needsConnection.length);
  for (const selector of needsConnection) {
    if (offered) await expect(page.locator(selector)).toBeVisible();
    else await expect(page.locator(selector)).toBeHidden();
  }
  await expect(page.locator('#connect-lastfm')).toBeVisible();
}

test('without a Last.fm connection the page offers only Connect, until connected and again after Disconnect', async ({ page }) => {
  const state = await installServer(page);
  state.connected = false;
  // As delivered, before the server said anything about the account.
  for (const selector of needsConnection) await expect(page.locator(selector)).toHaveJSProperty('hidden', true);
  await passwordLogin(page);
  await expect(page.locator('#connection-status')).toHaveText('Connect Last.fm to start.');
  await expectOffered(page, false);
  await expect(page.locator('#signed-in button:visible')).toHaveText(['Sign out', 'Connect Last.fm']);

  await page.getByRole('button', { name: 'Connect Last.fm', exact: true }).click();
  await expect(page.locator('#status')).toHaveText('Last.fm connected.');
  await expectOffered(page, true);

  await page.getByRole('button', { name: 'Disconnect', exact: true }).click();
  await page.getByRole('button', { name: 'Disconnect and clear plugin data' }).click();
  await expect(page.locator('#connection-status')).toHaveText('Connect Last.fm to start.');
  await expectOffered(page, false);
});

test('an account that must reconnect keeps its sections', async ({ page }) => {
  const state = await installServer(page);
  state.connected = true;
  state.needsReconnect = true;
  await passwordLogin(page);
  await expect(page.locator('#connection-status')).toContainText('reconnect required');
  await expectOffered(page, true);
});

test('signing out withdraws the sections before the next account is known', async ({ page }) => {
  await installServer(page);
  await passwordLogin(page);
  await expectOffered(page, true);
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await expect(page.locator('#sign-in')).toBeVisible();
  // The next user's connection is unknown until the server answered; the mark itself must be withdrawn, not only its container.
  for (const selector of needsConnection) await expect(page.locator(selector)).toHaveJSProperty('hidden', true);
});

for (const prefix of ['', '/jellyfin']) {
  test(`one button connects Last.fm and returns to the page at ${prefix || 'root'}`, async ({ page }) => {
    const state = await installServer(page, { prefix });
    state.connected = false;
    await passwordLogin(page);
    await expect(page.locator('#finish-connection, #finish-lastfm, #authorize-lastfm')).toHaveCount(0);
    await expect(page.locator('#connection button.primary')).toHaveCount(1);
    await page.getByRole('button', { name: 'Connect Last.fm', exact: true }).click();
    await expect(page.locator('#status')).toHaveText('Last.fm connected.');
    await expect(page.locator('#connection-status')).toContainText('Connected as Listener');
    await expect(page.getByRole('button', { name: 'Reconnect Last.fm', exact: true })).toBeVisible();
    expect(page.url()).toBe(`http://127.0.0.1:4177${prefix}/JellySin/Lastfm/`);
    expect(state.lastfm).toHaveLength(1);
    expect(told(state)).toHaveLength(0);
    const visit = new URL(state.lastfm[0]);
    expect([...visit.searchParams.keys()]).toEqual(['api_key', 'cb']);
    expect(visit.searchParams.get('cb')).toMatch(new RegExp(`^http://127\\.0\\.0\\.1:4177${prefix}/JellySin/Lastfm/Callback\\?attempt=[0-9a-f]{32}$`));
    const begin = state.requests.filter(request => request.path.endsWith('/Connection/Begin'));
    expect(begin.map(request => request.body)).toEqual([{ returnToSettings: false }]);
    const finish = state.requests.filter(request => request.path.endsWith('/Connection/Finish'));
    expect(finish.map(request => request.body)).toEqual([null]);
    // The token of Last.fm reaches the return address only; no page request or page address carries it.
    expect(state.requests.filter(request => request.url.includes(lastfmToken)).map(request => request.path)).toEqual(['/JellySin/Lastfm/Callback']);
    expect(await page.evaluate(() => globalThis.document.cookie)).toBe('');
    expect(state.requests.every(request => !request.url.includes(token))).toBe(true);
  });
}

test('a return that Last.fm did not approve says so, starts nothing and leaves a clean address', async ({ page }) => {
  const state = await installServer(page);
  state.connected = false;
  state.lastfmAnswer = 'none';
  await passwordLogin(page);
  await page.getByRole('button', { name: 'Connect Last.fm', exact: true }).click();
  await expect(page.locator('#status')).toHaveText('Last.fm did not connect. Start again.');
  await expect(page.locator('#status')).toHaveAttribute('role', 'alert');
  await expect(page.locator('#connection-status')).toContainText('Connect Last.fm to start');
  expect(page.url()).toBe('http://127.0.0.1:4177/JellySin/Lastfm/');
  expect(state.requests.filter(request => request.path.endsWith('/Connection/Finish'))).toHaveLength(0);
});

test('without a usable Last.fm application, connecting asks for an administrator instead of blaming the connection', async ({ page }) => {
  const state = await installServer(page);
  state.connected = false;
  state.applicationMissing = true;
  await passwordLogin(page);
  await page.getByRole('button', { name: 'Connect Last.fm', exact: true }).click();
  await expect(page.locator('#status')).toHaveText('This server has no usable Last.fm application. Ask a server administrator.');
  await expect(page.locator('#status')).toHaveAttribute('role', 'alert');
  expect(page.url()).toBe('http://127.0.0.1:4177/JellySin/Lastfm/');
});

test('an approval that Last.fm then refuses is reported and not repeated', async ({ page }) => {
  const state = await installServer(page);
  state.connected = false;
  state.finishFails = true;
  await passwordLogin(page);
  await page.getByRole('button', { name: 'Connect Last.fm', exact: true }).click();
  await expect(page.locator('#status')).toHaveText('Last.fm did not confirm the connection. Start again.');
  await expect(page.locator('#connection-status')).toContainText('Connect Last.fm to start');
  await expect(page.locator('#signed-in')).toBeVisible();
  expect(state.requests.filter(request => request.path.endsWith('/Connection/Finish'))).toHaveLength(1);
});

const back = path => `cb=${encodeURIComponent(`${path}?attempt=1`)}`;
const told = state => state.requests.filter(request => request.path === '/JellySin/Lastfm/Me/Connection/Refusal').map(({ method, body }) => ({ method, body }));
const home = back('http://127.0.0.1:4177/JellySin/Lastfm/Callback');
const notStarted = 'Last.fm sign-in could not start.';
const askAdministrator = `${notStarted} Ask the administrator of this server to check the server address setting.`;
const namedReturn = (server, expected) => `${notStarted} The server names ${server} as the return address, but this page expects ${expected}. Set the published server address (start option published-server-url) to the address users open, or add the proxy under Networking, Known proxies. Then restart Jellyfin.`;
// Each address breaks one rule only, so that every rule is needed to refuse its case. The third entry is the return
// address the server named, where that is the reason: a server behind a proxy that does not know its public scheme and
// host name produces it. A user is sent to the administrator; the administrator reads both addresses and the setting.
const refusedAddresses = [
  ['another host', `https://last.fm.attacker.example/api/auth/?api_key=fixture&${home}`],
  ['an unencrypted address', `http://www.last.fm/api/auth/?api_key=fixture&${home}`],
  ['another page of Last.fm', `https://www.last.fm/user/someone?api_key=fixture&${home}`],
  ['something that is no address', 'no address at all'],
  ['a return to another server', `https://www.last.fm/api/auth/?api_key=fixture&${back('https://attacker.example/JellySin/Lastfm/Callback')}`, 'https://attacker.example/JellySin/Lastfm/Callback'],
  ['a return over another scheme', `https://www.last.fm/api/auth/?api_key=fixture&${back('https://127.0.0.1:4177/JellySin/Lastfm/Callback')}`, 'https://127.0.0.1:4177/JellySin/Lastfm/Callback'],
  ['a return to another page of this server', `https://www.last.fm/api/auth/?api_key=fixture&${back('http://127.0.0.1:4177/JellySin/Lastfm/')}`, 'http://127.0.0.1:4177/JellySin/Lastfm/'],
  ['no return address', 'https://www.last.fm/api/auth/?api_key=fixture', 'http://127.0.0.1:4177/'],
];
for (const [reason, address, named] of refusedAddresses) {
  for (const administrator of named ? [false, true] : [false]) {
    test(administrator ? `an administrator reads why the page does not leave for ${reason}` : `the page does not leave for ${reason}`, async ({ page }) => {
      const state = await installServer(page, { administrator });
      state.connected = false;
      state.authorizationUrl = address;
      await passwordLogin(page);
      await page.getByRole('button', { name: 'Connect Last.fm', exact: true }).click();
      const text = !named ? notStarted : administrator ? namedReturn(named, 'http://127.0.0.1:4177/JellySin/Lastfm/Callback') : askAdministrator;
      await expect(page.locator('#status')).toHaveText(text);
      await expect(page.locator('#status')).toHaveAttribute('role', 'alert');
      expect(page.url()).toBe('http://127.0.0.1:4177/JellySin/Lastfm/');
      expect(state.lastfm).toHaveLength(0);
      // The server is told once, by a name from a fixed set, so that its log shows a click the page refused.
      expect(told(state)).toEqual([{ method: 'POST', body: { reason: named ? 'return-address' : 'approval-address' } }]);
    });
  }
}

test('the page shows its refusal also when the server does not take the note of it', async ({ page }) => {
  const state = await installServer(page);
  state.connected = false;
  state.authorizationUrl = refusedAddresses[0][1];
  state.refusalFails = true;
  await passwordLogin(page);
  await page.getByRole('button', { name: 'Connect Last.fm', exact: true }).click();
  await expect(page.locator('#status')).toHaveText(notStarted);
  expect(told(state)).toHaveLength(1);
});

test('administrators open native Jellyfin settings from the user page', async ({ page }) => {
  await installServer(page, { administrator: true });
  await passwordLogin(page);
  await expect(page.locator('#administrator')).toBeVisible();
  await expect(page.locator('#server-settings-link')).toHaveAttribute('href', '/web/index.html#/configurationpage?name=jellysin-lastfm');
  await expect(page.locator('#signed-in input[type=password]')).toHaveCount(0);
});

test('delivery exposes retained daily limits, rejected submissions and pending disk writes', async ({ page }) => {
  const state = await installServer(page);
  state.delivery = { ...state.delivery, LastIgnoredCode: 5, Rejected: 2, LastRejectedCode: 3, PendingPersistence: 1 };
  await passwordLogin(page);
  await page.getByText('Delivery status', { exact: true }).click();
  await page.locator('#delivery-refresh').click();
  await expect(page.locator('#delivery-status')).toContainText('daily limit reached; submissions retained');
  await expect(page.locator('#delivery-status')).toContainText('2 rejected by Last.fm (reason 3)');
  await expect(page.locator('#delivery-status')).toContainText('cannot yet survive a server restart');
  expect(state.requests.some(request => request.path.endsWith('/Delivery/Retry'))).toBe(false);
});

test('delivery explains that a play time ahead of Last.fm retries by itself', async ({ page }) => {
  const state = await installServer(page);
  state.delivery = { ...state.delivery, Pending: 2, Blocked: 1, LastIgnoredCode: 4 };
  await passwordLogin(page);
  await page.getByText('Delivery status', { exact: true }).click();
  await page.locator('#delivery-refresh').click();
  await expect(page.locator('#delivery-status')).toContainText('1 awaiting retry');
  await expect(page.locator('#delivery-status')).toContainText('play time ahead of its clock; retrying automatically');
  await expect(page.locator('#delivery-status')).not.toContainText('daily limit');
});

test('delivery warns once the oldest held play is seven days old', async ({ page }) => {
  const state = await installServer(page);
  state.delivery = { ...state.delivery, Pending: 3, OldestPendingDays: 6 };
  await passwordLogin(page);
  await page.getByText('Delivery status', { exact: true }).click();
  await page.locator('#delivery-refresh').click();
  await expect(page.locator('#delivery-status')).toContainText('3 saved for delivery');
  await expect(page.locator('#delivery-status')).not.toContainText('Oldest held play');
  state.delivery = { ...state.delivery, OldestPendingDays: 7 };
  await page.locator('#delivery-refresh').click();
  await expect(page.locator('#delivery-status')).toContainText('Oldest held play is 7 days old; Last.fm drops plays it finds too old');
  await expect(page.locator('#delivery-status')).not.toContainText('14');
});

test('keyboard sign-in, disclosure controls and cancellation preserve focus', async ({ page }) => {
  await installServer(page);
  await page.keyboard.press('Tab');
  await expect(page.getByRole('link', { name: 'Skip to music' })).toBeFocused();
  await page.keyboard.press('Enter');
  await page.keyboard.press('Tab');
  await expect(page.locator('#quick-connect')).toBeFocused();
  await page.keyboard.press('Tab');
  await page.keyboard.press('Enter');
  await page.keyboard.press('Tab');
  await expect(page.locator('#username')).toBeFocused();
  await page.keyboard.type('listener');
  await page.keyboard.press('Tab');
  await page.keyboard.type('fixture-password');
  await page.keyboard.press('Enter');
  await expect(page.locator('#signed-in')).toBeVisible();
  await page.locator('#disconnect-lastfm').focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('#confirm-disconnect')).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(page.locator('#cancel-disconnect')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.locator('#disconnect-confirmation')).toBeHidden();
  await expect(page.locator('#disconnect-lastfm')).toBeFocused();
});

test('an interrupted playlist can be reviewed and cancelled without deleting the playlist', async ({ page }) => {
  const state = await installServer(page);
  const operationId = '33333333-3333-4333-8333-333333333333';
  state.pendingPlaylist = { OperationId: operationId, Name: 'Evening listening', ItemCount: 20, Status: 'A track is no longer accessible.' };
  await passwordLogin(page);
  await page.locator('#playlists-refresh').click();
  await expect(page.locator('#playlist-pending')).toContainText('no longer accessible');
  await page.getByRole('button', { name: 'Review cancellation' }).click();
  await expect(page.locator('#playlist-pending')).toContainText('possibly partial state');
  expect(state.pendingPlaylist).not.toBeNull();
  await page.getByRole('button', { name: 'Keep waiting' }).click();
  await expect(page.locator('#playlist-pending')).toContainText('Evening listening');
  await page.getByRole('button', { name: 'Review cancellation' }).click();
  await page.getByRole('button', { name: 'Cancel update and pause refresh' }).click();
  await expect(page.locator('#playlist-pending')).toBeEmpty();
  expect(state.pendingPlaylist).toBeNull();
  const deletes = state.requests.filter(request => request.method === 'DELETE');
  expect(deletes.map(request => request.path)).toEqual([`/JellySin/Lastfm/Me/Playlists/Pending/${operationId}`]);
});

/** The administrator page as Jellyfin shows it: a fragment in the host's own interface, with the host's client. */
async function openSettings(page, options = {}) {
  const setup = { source: 'Project', connection: {}, server: '', hash: '#/configurationpage?name=jellysin-lastfm', authorizationUrl: 'https://www.last.fm/api/auth/?api_key=fixture&cb=http%3A%2F%2F127.0.0.1%3A4177%2Fjellyfin%2FJellySin%2FLastfm%2FCallback%3Fattempt%3D1', ...options };
  await page.addInitScript(input => {
    const config = { Enabled: true, HostSetting: 'preserved', ...input.config };
    // The calls outlive a visit to Last.fm in the same tab, so that a test can read what the page sent before it left.
    const state = { calls: JSON.parse(sessionStorage.getItem('fixture-calls') ?? '[]'), application: { Configured: input.source === 'Project' || input.source === 'Override', Source: input.source },
      connection: { Connected: false, Username: null, NeedsReconnect: false, ScrobblingEnabled: true, ApprovalPending: false, ...input.connection } };
    globalThis.fixture = state;
    const answer = (path, type, data) => {
      if (path === 'Admin/Application' && type === 'GET') return state.application;
      if (path === 'Admin/Application') { state.application = { Configured: true, Source: type === 'PUT' ? 'Override' : 'Project' }; return undefined; }
      if (path === 'Admin/Libraries' && input.libraries) return input.libraries;
      if (path === 'Me') return { UserName: 'administrator', IsAdministrator: true, Connection: state.connection };
      if (path === 'Me/Connection/Begin' && input.beginStatus) throw new Response(null, { status: input.beginStatus });
      if (path === 'Me/Connection/Begin' && data?.ReturnToSettings === true) return { AuthorizationUrl: input.authorizationUrl, ExpiresAt: '2026-09-08T15:10:00Z' };
      if (path === 'Me/Connection/Finish' && state.connection.ApprovalPending && !input.finishFails) {
        state.connection = { ...state.connection, Connected: true, Username: 'Listener', ApprovalPending: false };
        return state.connection;
      }
      if (path === 'Me/Connection/Refusal' && type === 'POST' && input.refusalStatus) throw new Response(null, { status: input.refusalStatus });
      if (path === 'Me/Connection/Refusal' && type === 'POST') return undefined;
      throw new Error('No fixture response');
    };
    globalThis.ApiClient = {
      getUrl: path => input.server + '/jellyfin/' + path,
      getPluginConfiguration: async () => ({ ...config }),
      updatePluginConfiguration: async (_id, saved) => { state.savedConfig = saved; Object.assign(config, saved); },
      ajax: async request => {
        const path = request.url.split('/JellySin/Lastfm/')[1];
        const data = request.data ? JSON.parse(request.data) : null;
        state.calls.push({ type: request.type, path, data });
        sessionStorage.setItem('fixture-calls', JSON.stringify(state.calls));
        return answer(path, request.type, data);
      },
    };
  }, setup);
  const fragment = await readFile(new URL('../../src/JellySin.Plugin.Lastfm/Web/admin.html', import.meta.url), 'utf8');
  await page.route('http://127.0.0.1:4177/native', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><html lang="en"><title>Native settings test</title><body>' + fragment + '</body></html>' }));
  await page.route('https://www.last.fm/**', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Last.fm</title>' }));
  await page.goto('http://127.0.0.1:4177/native' + setup.hash);
  await page.locator('#jellysin-admin').dispatchEvent('pageshow');
}

test('native admin settings save the library rule only when the libraries could be listed', async ({ page }) => {
  const music = '11111111-1111-1111-1111-111111111111';
  const books = '22222222-2222-2222-2222-222222222222';
  await openSettings(page, { config: { LibraryMode: 2, LibraryIds: [books] }, libraries: [{ Id: music, Name: 'Music' }, { Id: books, Name: 'Audiobooks' }] });
  await expect(page.locator('#jellysin-library-mode')).toHaveValue('BlockList');
  await expect(page.getByLabel('Audiobooks', { exact: true })).toBeChecked();
  await page.getByLabel('Music', { exact: true }).check();
  await page.locator('#jellysin-library-mode').selectOption('AllowList');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#jellysin-settings-status')).toHaveText('Settings saved.');
  expect(await page.evaluate(() => globalThis.fixture.savedConfig)).toEqual({ Enabled: true, HostSetting: 'preserved', ScrobbleMusic: true, ScrobbleAudiobooks: false, ScrobbleMusicVideos: false, LibraryMode: 1, LibraryIds: [music, books] , ScrobblePercent: 50, ScrobbleCeilingSeconds: 240, ScrobbleMinimumLengthSeconds: 30, ScrobbleAllArtists: false, RemoveFeaturedArtists: false, RequireMusicBrainzId: false, MetadataRules: [], BlockRules: [] });
});

test('native admin settings keep the library rule when the libraries could not be listed', async ({ page }) => {
  const books = '22222222-2222-2222-2222-222222222222';
  await openSettings(page, { config: { LibraryMode: 2, LibraryIds: [books] } });
  await expect(page.locator('#jellysin-libraries-list')).toHaveText('Could not load libraries.');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#jellysin-settings-status')).toHaveText('Settings saved.');
  expect(await page.evaluate(() => globalThis.fixture.savedConfig)).toEqual({ Enabled: true, HostSetting: 'preserved', ScrobbleMusic: true, ScrobbleAudiobooks: false, ScrobbleMusicVideos: false, LibraryMode: 2, LibraryIds: [books] , ScrobblePercent: 50, ScrobbleCeilingSeconds: 240, ScrobbleMinimumLengthSeconds: 30, ScrobbleAllArtists: false, RemoveFeaturedArtists: false, RequireMusicBrainzId: false, MetadataRules: [], BlockRules: [] });
});

test('native admin settings save the media types to scrobble', async ({ page }) => {
  await openSettings(page, { config: { ScrobbleMusic: true, ScrobbleAudiobooks: false, ScrobbleMusicVideos: false } });
  await expect(page.getByLabel('Music tracks')).toBeChecked();
  await expect(page.getByLabel('Audiobook chapters')).not.toBeChecked();
  await expect(page.getByLabel('Music videos')).not.toBeChecked();
  await page.getByLabel('Music tracks').uncheck();
  await page.getByLabel('Audiobook chapters').check();
  await page.getByLabel('Music videos').check();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#jellysin-settings-status')).toHaveText('Settings saved.');
  expect(await page.evaluate(() => globalThis.fixture.savedConfig)).toEqual({ Enabled: true, HostSetting: 'preserved', ScrobbleMusic: false, ScrobbleAudiobooks: true, ScrobbleMusicVideos: true , ScrobblePercent: 50, ScrobbleCeilingSeconds: 240, ScrobbleMinimumLengthSeconds: 30, ScrobbleAllArtists: false, RemoveFeaturedArtists: false, RequireMusicBrainzId: false, MetadataRules: [], BlockRules: [] });
});

test('native admin settings show the media type defaults for a configuration saved before they existed', async ({ page }) => {
  await openSettings(page);
  await expect(page.getByLabel('Music tracks')).toBeChecked();
  await expect(page.getByLabel('Audiobook chapters')).not.toBeChecked();
  await expect(page.getByLabel('Music videos')).not.toBeChecked();
});

test('native admin settings save a stricter scrobble threshold', async ({ page }) => {
  await openSettings(page);
  await expect(page.getByLabel(/Share of the track to hear/)).toHaveValue('50');
  await expect(page.getByLabel(/Count any track after/)).toHaveValue('240');
  await expect(page.getByLabel(/Ignore tracks up to/)).toHaveValue('30');
  await page.getByLabel(/Share of the track to hear/).fill('100');
  await page.getByLabel(/Count any track after/).fill('7200');
  await page.getByLabel(/Ignore tracks up to/).fill('600');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#jellysin-settings-status')).toHaveText('Settings saved.');
  expect(await page.evaluate(() => globalThis.fixture.savedConfig)).toMatchObject({ ScrobblePercent: 100, ScrobbleCeilingSeconds: 7200, ScrobbleMinimumLengthSeconds: 600 });
});

test('native admin settings refuse a scrobble threshold looser than Last.fm', async ({ page }) => {
  await openSettings(page);
  await page.getByLabel(/Share of the track to hear/).fill('49');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#jellysin-settings-status')).toHaveText('');
  expect(await page.evaluate(() => globalThis.fixture.savedConfig)).toBeUndefined();
});

test('native admin settings show a stored threshold outside the range as the nearest allowed value', async ({ page }) => {
  await openSettings(page, { config: { ScrobblePercent: 10, ScrobbleCeilingSeconds: 99999, ScrobbleMinimumLengthSeconds: 'x' } });
  await expect(page.getByLabel(/Share of the track to hear/)).toHaveValue('50');
  await expect(page.getByLabel(/Count any track after/)).toHaveValue('7200');
  await expect(page.getByLabel(/Ignore tracks up to/)).toHaveValue('30');
});

test('native admin settings save the joined artist credit option', async ({ page }) => {
  await openSettings(page);
  await expect(page.getByLabel('Send every credited artist')).not.toBeChecked();
  await page.getByLabel('Send every credited artist').check();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#jellysin-settings-status')).toHaveText('Settings saved.');
  expect(await page.evaluate(() => globalThis.fixture.savedConfig)).toMatchObject({ ScrobbleAllArtists: true });
});

test('native admin settings show a stored joined artist credit option', async ({ page }) => {
  await openSettings(page, { config: { ScrobbleAllArtists: true } });
  await expect(page.getByLabel('Send every credited artist')).toBeChecked();
});

test('native admin settings save the remove featured artists option', async ({ page }) => {
  await openSettings(page);
  await expect(page.getByLabel('Remove featured artists')).not.toBeChecked();
  await page.getByLabel('Remove featured artists').check();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#jellysin-settings-status')).toHaveText('Settings saved.');
  expect(await page.evaluate(() => globalThis.fixture.savedConfig)).toMatchObject({ RemoveFeaturedArtists: true });
});

test('native admin settings show a stored remove featured artists option', async ({ page }) => {
  await openSettings(page, { config: { RemoveFeaturedArtists: true } });
  await expect(page.getByLabel('Remove featured artists')).toBeChecked();
});

test('native admin settings save and reload the require musicbrainz id option', async ({ page }) => {
  await openSettings(page);
  await expect(page.getByLabel('Scrobble only tracks with a MusicBrainz recording ID')).not.toBeChecked();
  await page.getByLabel('Scrobble only tracks with a MusicBrainz recording ID').check();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#jellysin-settings-status')).toHaveText('Settings saved.');
  expect(await page.evaluate(() => globalThis.fixture.savedConfig)).toMatchObject({ RequireMusicBrainzId: true });

  await page.locator('#jellysin-admin').dispatchEvent('pageshow');
  await expect(page.getByLabel('Scrobble only tracks with a MusicBrainz recording ID')).toBeChecked();
});

test('native admin settings list, add, remove and save metadata cleanup rules', async ({ page }) => {
  // Jellyfin may send an enum by name or by number; the page saves numbers.
  await openSettings(page, {
    config: {
      MetadataRules: [
        { Field: 'AlbumArtist', Kind: 'Correction', Match: 'Beatles', Replacement: 'The Beatles' },
        { Field: 0, Kind: 0, Match: 'Old', Replacement: 'Gone' }
      ]
    }
  });
  const rows = page.locator('.jellysin-rule-row');
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(0).locator('.jellysin-rule-field')).toHaveValue('3');
  await expect(rows.nth(0).locator('.jellysin-rule-match')).toHaveValue('Beatles');
  await expect(rows.nth(0).locator('.jellysin-rule-replacement')).toHaveValue('The Beatles');

  await rows.nth(1).locator('.jellysin-remove-rule').click();
  await expect(rows).toHaveCount(1);

  await page.locator('#jellysin-add-rule').click();
  const added = rows.nth(1);
  await added.locator('.jellysin-rule-field').selectOption('Title');
  await added.locator('.jellysin-rule-kind').selectOption('Pattern');
  await added.locator('.jellysin-rule-match').fill(' \\(Remastered\\)$');
  // A row left without a match is not saved.
  await page.locator('#jellysin-add-rule').click();
  await expect(rows).toHaveCount(3);

  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#jellysin-settings-status')).toHaveText('Settings saved.');

  expect(await page.evaluate(() => globalThis.fixture.savedConfig.MetadataRules)).toEqual([
    { Field: 3, Kind: 0, Match: 'Beatles', Replacement: 'The Beatles' },
    { Field: 1, Kind: 1, Match: ' \\(Remastered\\)$', Replacement: '' }
  ]);
});

test('native admin settings list, add, remove, save and reload block rules', async ({ page }) => {
  // Jellyfin may send an enum by name or by number; the page saves numbers.
  await openSettings(page, {
    config: {
      BlockRules: [
        { Field: 'Rating', Value: 'Explicit' },
        { Field: 0, Value: 'Podcast' }
      ]
    }
  });
  const rows = page.locator('.jellysin-block-rule-row');
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(0).locator('.jellysin-block-rule-field')).toHaveValue('3');
  await expect(rows.nth(0).locator('.jellysin-block-rule-value')).toHaveValue('Explicit');
  await expect(rows.nth(1).locator('.jellysin-block-rule-field')).toHaveValue('0');
  await expect(rows.nth(1).locator('.jellysin-block-rule-value')).toHaveValue('Podcast');

  await rows.nth(1).locator('.jellysin-remove-rule').click();
  await expect(rows).toHaveCount(1);

  await page.locator('#jellysin-add-block-rule').click();
  const added = rows.nth(1);
  await added.locator('.jellysin-block-rule-field').selectOption('Artist');
  await added.locator('.jellysin-block-rule-value').fill('Blocked Artist');
  // A row left without a value is not saved.
  await page.locator('#jellysin-add-block-rule').click();
  await expect(rows).toHaveCount(3);

  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#jellysin-settings-status')).toHaveText('Settings saved.');

  const saved = await page.evaluate(() => globalThis.fixture.savedConfig);
  expect(saved.BlockRules).toEqual([
    { Field: 3, Value: 'Explicit' },
    { Field: 1, Value: 'Blocked Artist' }
  ]);

  // Reload the page settings with saved configuration.
  await page.locator('#jellysin-admin').dispatchEvent('pageshow');
  const reloadedRows = page.locator('.jellysin-block-rule-row');
  await expect(reloadedRows).toHaveCount(2);
  await expect(reloadedRows.nth(0).locator('.jellysin-block-rule-field')).toHaveValue('3');
  await expect(reloadedRows.nth(0).locator('.jellysin-block-rule-value')).toHaveValue('Explicit');
  await expect(reloadedRows.nth(1).locator('.jellysin-block-rule-field')).toHaveValue('1');
  await expect(reloadedRows.nth(1).locator('.jellysin-block-rule-value')).toHaveValue('Blocked Artist');
});

const calls = (page, path) => page.evaluate(wanted => globalThis.fixture.calls.filter(call => call.path === wanted), path);

test('native admin settings preserve host configuration and clear application secrets', async ({ page }) => {
  await openSettings(page);
  await expect(page.locator('#jellysin-enabled')).toBeChecked();
  await expect(page.locator('#jellysin-settings input')).toHaveCount(10);
  await page.locator('#jellysin-enabled').uncheck();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#jellysin-settings-status')).toHaveText('Settings saved.');
  expect(await page.evaluate(() => globalThis.fixture.savedConfig)).toEqual({ Enabled: false, HostSetting: 'preserved', ScrobbleMusic: true, ScrobbleAudiobooks: false, ScrobbleMusicVideos: false , ScrobblePercent: 50, ScrobbleCeilingSeconds: 240, ScrobbleMinimumLengthSeconds: 30, ScrobbleAllArtists: false, RemoveFeaturedArtists: false, RequireMusicBrainzId: false, MetadataRules: [], BlockRules: [] });
  // The application fields are for the rare server with its own application: closed until asked for.
  await expect(page.locator('#jellysin-advanced')).not.toHaveAttribute('open');
  await expect(page.getByLabel('Application API key', { exact: true })).toBeHidden();
  await page.locator('#jellysin-advanced > summary').click();
  await expect(page.locator('#jellysin-application-status')).toHaveText('In use: the Last.fm application included with this build.');
  await expect(page.getByRole('button', { name: 'Remove override', exact: true })).toBeHidden();
  await page.getByLabel('Application API key', { exact: true }).fill('b'.repeat(32));
  await page.getByLabel('Application shared secret').fill('c'.repeat(32));
  await page.getByRole('button', { name: 'Save application override' }).click();
  await expect(page.locator('#jellysin-application-status')).toHaveText('In use: an application override stored on this server. Connected accounts must connect again after a change.');
  await expect(page.locator('#jellysin-api-key')).toHaveValue('');
  await expect(page.locator('#jellysin-api-secret')).toHaveValue('');
  expect((await calls(page, 'Admin/Application')).find(call => call.type === 'PUT').data).toEqual({ ApiKey: 'b'.repeat(32), Secret: 'c'.repeat(32) });
  await expect(page.getByRole('button', { name: 'Remove override', exact: true })).toBeVisible();
});

test('an application override is removed only after a second, explicit step', async ({ page }) => {
  await openSettings(page, { source: 'Override' });
  await page.locator('#jellysin-advanced > summary').click();
  await expect(page.locator('#jellysin-application-status')).toContainText('In use: an application override stored on this server.');
  await page.getByRole('button', { name: 'Remove override', exact: true }).click();
  await expect(page.locator('#jellysin-remove-confirmation')).toContainText('Connected accounts must connect again');
  await expect(page.locator('#jellysin-confirm-remove')).toBeFocused();
  await page.getByRole('button', { name: 'Keep the override' }).click();
  await expect(page.locator('#jellysin-remove-confirmation')).toBeHidden();
  expect((await calls(page, 'Admin/Application')).filter(call => call.type === 'DELETE')).toHaveLength(0);
  await page.getByRole('button', { name: 'Remove override', exact: true }).click();
  await page.locator('#jellysin-confirm-remove').click();
  await expect(page.locator('#jellysin-application-status')).toHaveText('In use: the Last.fm application included with this build.');
  await expect(page.getByRole('button', { name: 'Remove override', exact: true })).toBeHidden();
  await expect(page.locator('#jellysin-remove-confirmation')).toBeHidden();
  expect((await calls(page, 'Admin/Application')).filter(call => call.type === 'DELETE')).toHaveLength(1);
});

test('an override the server cannot read asks for the application again and offers no connection', async ({ page }) => {
  await openSettings(page, { source: 'Unreadable', connection: { Connected: true, Username: 'Listener', NeedsReconnect: true } });
  await expect(page.locator('#jellysin-advanced')).toHaveAttribute('open', '');
  await expect(page.locator('#jellysin-application-status')).toContainText('cannot be read with its keys');
  await expect(page.getByRole('button', { name: 'Remove override', exact: true })).toBeVisible();
  await expect(page.locator('#jellysin-connect')).toBeHidden();
  await expect(page.locator('#jellysin-connection-status')).toHaveText(
    'Last.fm is connected as Listener. Last.fm asks you to connect again. Add a Last.fm application under Advanced before connecting.');
});

test('the administrator connects the own account with one button on the settings page', async ({ page }) => {
  await openSettings(page);
  await expect(page.locator('#jellysin-connection-status')).toHaveText('Last.fm is not connected.');
  await expect(page.locator('#jellysin-admin button.button-submit:visible')).toHaveCount(2);
  await page.getByRole('button', { name: 'Connect Last.fm', exact: true }).click();
  await page.waitForURL('https://www.last.fm/api/auth/?api_key=fixture&cb=**');
  await page.goBack();
  expect(await calls(page, 'Me/Connection/Begin')).toHaveLength(1);
  expect(await calls(page, 'Me/Connection/Finish')).toHaveLength(0);
  expect(await calls(page, 'Me/Connection/Refusal')).toHaveLength(0);
});

test('the settings page finishes an approved sign-in by itself after the return', async ({ page }) => {
  await openSettings(page, { connection: { ApprovalPending: true } });
  await expect(page.locator('#jellysin-connection-status')).toHaveText('Last.fm is connected as Listener.');
  await expect(page.getByRole('button', { name: 'Reconnect Last.fm', exact: true })).toBeVisible();
  expect(await calls(page, 'Me/Connection/Finish')).toEqual([{ type: 'POST', path: 'Me/Connection/Finish', data: null }]);
  expect(await calls(page, 'Me/Connection/Begin')).toHaveLength(0);
});

const refusals = [
  { name: 'a refused return', options: { hash: '#/configurationpage?name=jellysin-lastfm&connection=failed' }, text: 'Last.fm did not connect. Start again.', finishes: 0 },
  { name: 'an approval that Last.fm then refuses', options: { connection: { ApprovalPending: true }, finishFails: true }, text: 'Last.fm did not confirm the connection. Start again.', finishes: 1 },
];
for (const refusal of refusals) {
  test(`the settings page reports ${refusal.name} and stays ready for a new start`, async ({ page }) => {
    await openSettings(page, refusal.options);
    await expect(page.locator('#jellysin-connection-status')).toHaveText(refusal.text);
    await expect(page.getByRole('button', { name: 'Connect Last.fm', exact: true })).toBeEnabled();
    expect(await calls(page, 'Me/Connection/Finish')).toHaveLength(refusal.finishes);
  });
}

const settingsHome = back('http://127.0.0.1:4177/jellyfin/JellySin/Lastfm/Callback');
// As on the dashboard: each address breaks one rule only. Whoever opens this page is an administrator and reads both addresses.
for (const [reason, address, named] of [
  ['another host', `https://last.fm.attacker.example/api/auth/?api_key=fixture&${settingsHome}`],
  ['an unencrypted address', `http://www.last.fm/api/auth/?api_key=fixture&${settingsHome}`],
  ['another page of Last.fm', `https://www.last.fm/user/someone?api_key=fixture&${settingsHome}`],
  ['something that is no address', 'no address at all'],
  ['a return to another server', `https://www.last.fm/api/auth/?api_key=fixture&${back('http://10.0.0.5:8096/jellyfin/JellySin/Lastfm/Callback')}`, 'http://10.0.0.5:8096/jellyfin/JellySin/Lastfm/Callback'],
  ['a return over another scheme', `https://www.last.fm/api/auth/?api_key=fixture&${back('https://127.0.0.1:4177/jellyfin/JellySin/Lastfm/Callback')}`, 'https://127.0.0.1:4177/jellyfin/JellySin/Lastfm/Callback'],
  ['a return to another page of this server', `https://www.last.fm/api/auth/?api_key=fixture&${back('http://127.0.0.1:4177/jellyfin/JellySin/Lastfm/')}`, 'http://127.0.0.1:4177/jellyfin/JellySin/Lastfm/'],
  ['no return address', 'https://www.last.fm/api/auth/?api_key=fixture', 'http://127.0.0.1:4177/'],
]) {
  test(`the settings page does not leave for ${reason}`, async ({ page }) => {
    await openSettings(page, { authorizationUrl: address });
    await page.getByRole('button', { name: 'Connect Last.fm', exact: true }).click();
    await expect(page.locator('#jellysin-connection-status')).toHaveText(named ? namedReturn(named, 'http://127.0.0.1:4177/jellyfin/JellySin/Lastfm/Callback') : notStarted);
    await expect(page.getByRole('button', { name: 'Connect Last.fm', exact: true })).toBeEnabled();
    expect(page.url()).toContain('http://127.0.0.1:4177/native');
    expect(await calls(page, 'Me/Connection/Refusal')).toEqual([{ type: 'POST', path: 'Me/Connection/Refusal', data: { Reason: named ? 'return-address' : 'approval-address' } }]);
  });
}

test('the settings page shows its refusal also when the server does not take the note of it', async ({ page }) => {
  await openSettings(page, { authorizationUrl: 'https://last.fm.attacker.example/api/auth/?api_key=fixture', refusalStatus: 503 });
  await page.getByRole('button', { name: 'Connect Last.fm', exact: true }).click();
  await expect(page.locator('#jellysin-connection-status')).toHaveText(notStarted);
  await expect(page.getByRole('button', { name: 'Connect Last.fm', exact: true })).toBeEnabled();
  expect(await calls(page, 'Me/Connection/Refusal')).toHaveLength(1);
});

// The server's own refusal already left a line in its log; the page names the status and sends no second note.
for (const status of [403, 400]) {
  test(`the settings page names HTTP ${status} when the server refuses the start`, async ({ page }) => {
    await openSettings(page, { beginStatus: status });
    await page.getByRole('button', { name: 'Connect Last.fm', exact: true }).click();
    await expect(page.locator('#jellysin-connection-status')).toHaveText(`${notStarted} The server refused the request (HTTP ${status}); the server log names the reason.`);
    await expect(page.getByRole('button', { name: 'Connect Last.fm', exact: true })).toBeEnabled();
    expect(await calls(page, 'Me/Connection/Refusal')).toHaveLength(0);
  });
}

test('the settings page offers no sign-in that cannot return: no application, or an interface served from elsewhere', async ({ page, context }) => {
  await openSettings(page, { source: 'None' });
  await expect(page.locator('#jellysin-connection-status')).toHaveText('Add a Last.fm application under Advanced before connecting.');
  await expect(page.locator('#jellysin-connect')).toBeHidden();
  await expect(page.locator('#jellysin-advanced')).toHaveAttribute('open');
  await expect(page.locator('#jellysin-application-status')).toHaveText('No Last.fm application is available on this server.');
  const elsewhere = await context.newPage();
  await openSettings(elsewhere, { server: 'https://media.example' });
  await expect(elsewhere.locator('#jellysin-connection-status')).toHaveText('Last.fm is not connected. Connect from the music dashboard of this server.');
  await expect(elsewhere.locator('#jellysin-connect')).toBeHidden();
  await expect(elsewhere.locator('#jellysin-dashboard-link')).toHaveAttribute('href', 'https://media.example/jellyfin/JellySin/Lastfm/');
});

for (const width of [320, 1280]) {
  test(`accessible interface and bounded layout at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await installServer(page, { administrator: true });
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
    await passwordLogin(page);
    for (const id of ['charts-refresh', 'history-refresh', 'discovery-refresh', 'favourites-refresh', 'playlists-refresh']) {
      await page.locator(`#${id}`).click();
    }
    await page.getByText('Import play counts into Jellyfin', { exact: true }).click();
    await page.locator('#history-preview').click();
    await expect(page.locator('#preview-pagination')).toBeVisible();
    expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
    expect(await page.evaluate(() => globalThis.document.documentElement.scrollWidth <= globalThis.innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`dashboard-${width}.png`), fullPage: true });
  });
}

test('toggling a device scrobble checkbox updates the excluded devices list', async ({ page }) => {
  const state = await installServer(page);
  await passwordLogin(page);
  await expect(page.locator('#device-exclusions-list label')).toHaveCount(2);
  await expect(page.locator('#device-exclusions-list')).toContainText('Living Room TV (Android TV)');
  await expect(page.locator('#device-exclusions-list')).toContainText('My Phone (Mobile)');

  const livingRoomCheckbox = page.locator('input[data-device-id="dev-1"]');
  await expect(livingRoomCheckbox).toBeChecked();
  await livingRoomCheckbox.uncheck();

  await expect(page.locator('#status')).toHaveText('Device scrobbling rules updated.');
  expect(state.excludedDevices).toEqual(['dev-1']);
});

test('admin-blocked media type is rendered disabled and unchecked with admin text', async ({ page }) => {
  const state = await installServer(page);
  state.adminMediaTypes = { ScrobbleMusic: true, ScrobbleAudiobooks: false, ScrobbleMusicVideos: true };
  await passwordLogin(page);

  await expect(page.locator('#media-type-exclusions-list label')).toHaveCount(3);
  const audiobooksCheckbox = page.locator('input[data-media-type="Audiobooks"]');
  await expect(audiobooksCheckbox).toBeDisabled();
  await expect(audiobooksCheckbox).not.toBeChecked();
  await expect(page.locator('#media-type-exclusions-list')).toContainText('Turned off by the administrator');

  const musicCheckbox = page.locator('input[data-media-type="Music"]');
  await expect(musicCheckbox).toBeEnabled();
  await expect(musicCheckbox).toBeChecked();
});

test('device load failure displays safe load failure text', async ({ page }) => {
  const state = await installServer(page);
  state.deviceFail = true;
  await passwordLogin(page);

  await expect(page.locator('#device-exclusions-summary')).toHaveText('Could not load devices.');
});

test('saving media types keeps the user exclusion of a type the administrator turned off', async ({ page }) => {
  const state = await installServer(page);
  state.adminMediaTypes = { ScrobbleMusic: true, ScrobbleAudiobooks: false, ScrobbleMusicVideos: true };
  state.excludedMediaTypes = ['Audiobooks'];
  await passwordLogin(page);

  await page.locator('input[data-media-type="MusicVideos"]').uncheck();
  await expect(page.locator('#status')).toHaveText('Media type scrobbling rules updated.');
  expect(state.excludedMediaTypes).toEqual(['Audiobooks', 'MusicVideos']);
});

test('saving media types does not exclude a type the administrator turned off that the user kept', async ({ page }) => {
  const state = await installServer(page);
  state.adminMediaTypes = { ScrobbleMusic: true, ScrobbleAudiobooks: false, ScrobbleMusicVideos: true };
  state.excludedMediaTypes = [];
  await passwordLogin(page);

  await page.locator('input[data-media-type="MusicVideos"]').uncheck();
  await expect(page.locator('#status')).toHaveText('Media type scrobbling rules updated.');
  expect(state.excludedMediaTypes).toEqual(['MusicVideos']);
});

test('scrobble log toggle on saves preference and reload shows on', async ({ page }) => {
  const state = await installServer(page);
  await passwordLogin(page);

  const checkbox = page.locator('#scrobble-log-enabled');
  await expect(checkbox).not.toBeChecked();
  await expect(page.locator('#scrobble-log-count')).toHaveText('0 entries logged.');

  await checkbox.check();
  await expect(page.locator('#status')).toHaveText('Scrobble log enabled.');
  expect(state.scrobbleLogEnabled).toBe(true);

  await page.reload();
  await expect(page.locator('#signed-in')).toBeVisible();
  await expect(page.locator('#scrobble-log-enabled')).toBeChecked();
});

