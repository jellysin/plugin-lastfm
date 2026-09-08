import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
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
  expect(state.imported).toBe(false);
  await page.getByRole('button', { name: 'Next changes' }).click();
  await expect(page.locator('#history-preview-list')).toContainText('Angel');
  await page.getByRole('button', { name: 'Resume saved preview' }).click();
  await expect(page.locator('#preview-page')).toHaveText('Preview page 1 of 2');
  expect(state.imported).toBe(false);
  await page.getByRole('button', { name: 'Continue preview', exact: true }).click();
  await expect(page.locator('#history-continue')).toBeHidden();
  await page.getByRole('button', { name: 'Confirm and import preview' }).click();
  await expect(page.locator('#history-apply')).toBeDisabled();
  expect(state.imported).toBe(true);
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

test('admin credential replacement clears secrets from the form', async ({ page }) => {
  const state = await installServer(page, { administrator: true });
  await passwordLogin(page);
  await expect(page.locator('#administrator')).toBeVisible();
  await page.getByLabel('Application API key', { exact: true }).fill('b'.repeat(32));
  await page.getByLabel('Application shared secret').fill('c'.repeat(32));
  await page.getByRole('button', { name: 'Save application credentials' }).click();
  await expect(page.locator('#status')).toContainText('Application credentials saved');
  await expect(page.locator('#api-key')).toHaveValue('');
  await expect(page.locator('#api-secret')).toHaveValue('');
  expect(state.requests.find(request => request.path.endsWith('Admin/Application') && request.method === 'PUT').body.secret).toBe('c'.repeat(32));
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
