import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { readFile } from 'node:fs/promises';
import { installServer, passwordLogin, token } from './fixture.mjs';

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

test('native admin settings preserve host configuration and clear application secrets', async ({ page }) => {
  await page.addInitScript(() => {
    const state = { Enabled: true, MetadataEnabled: false, SimilarityEnabled: false, HostSetting: 'preserved' };
    globalThis.ApiClient = {
      getUrl: path => '/jellyfin/' + path,
      getPluginConfiguration: async () => ({ ...state }),
      updatePluginConfiguration: async (_id, input) => { globalThis.savedConfig = input; },
      ajax: async input => {
        if (input.type === 'PUT') globalThis.savedApplication = JSON.parse(input.data);
        return { Configured: true };
      },
    };
  });
  const fragment = await readFile(new URL('../../src/JellySin.Plugin.Lastfm/Web/admin.html', import.meta.url), 'utf8');
  await page.route('http://127.0.0.1:4177/native', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><html lang="en"><title>Native settings test</title><body>' + fragment + '</body></html>' }));
  await page.goto('http://127.0.0.1:4177/native');
  await page.locator('#jellysin-admin').dispatchEvent('pageshow');
  await expect(page.locator('#jellysin-application-status')).toHaveText('Application credentials are configured.');
  await expect(page.locator('#jellysin-enabled')).toBeChecked();
  await page.locator('#jellysin-enabled').uncheck();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#jellysin-settings-status')).toHaveText('Settings saved.');
  expect(await page.evaluate(() => globalThis.savedConfig)).toEqual({ Enabled: false, MetadataEnabled: false, SimilarityEnabled: false, HostSetting: 'preserved' });
  await page.getByLabel('Application API key', { exact: true }).fill('b'.repeat(32));
  await page.getByLabel('Application shared secret').fill('c'.repeat(32));
  await page.getByRole('button', { name: 'Save application override' }).click();
  await expect(page.locator('#jellysin-application-status')).toContainText('Application credentials saved');
  await expect(page.locator('#jellysin-api-key')).toHaveValue('');
  await expect(page.locator('#jellysin-api-secret')).toHaveValue('');
  expect(await page.evaluate(() => globalThis.savedApplication.Secret)).toBe('c'.repeat(32));
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
