import { test, expect } from '@playwright/test';
import { installServer, passwordLogin, token, track } from './fixture.mjs';

function deferred() {
  let resolve;
  const promise = new Promise(complete => { resolve = complete; });
  return { promise, resolve };
}

test('Quick Connect supersedes a pending password response', async ({ page }) => {
  const state = await installServer(page);
  await page.clock.install();
  const requested = deferred();
  const reply = deferred();
  await page.route('**/Users/AuthenticateByName', async route => {
    requested.resolve();
    await reply.promise;
    await route.fulfill({ json: { AccessToken: 'b'.repeat(32), ServerId: 'fixture-server', User: { Name: 'old-account' } } });
  });
  await page.getByText('Sign in with a password', { exact: true }).click();
  await page.getByLabel('Jellyfin username').fill('old-account');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await requested.promise;
  await page.getByRole('button', { name: 'Use Quick Connect' }).click();
  await expect(page.locator('#quick-code')).toHaveText('123456');
  reply.resolve();
  await expect(page.locator('#signed-in')).toBeHidden();
  await page.clock.fastForward(3000);
  await expect(page.locator('#signed-in')).toBeVisible();
  await expect(page.locator('#account-label')).toHaveText('listener');
  expect(state.requests.filter(request => request.path.endsWith('/Me')).every(request => request.headers.authorization.includes(token))).toBe(true);
  expect(state.requests.filter(request => request.path.endsWith('AuthenticateWithQuickConnect'))).toHaveLength(1);
});

test('sign out clears private views before a delayed server revocation finishes', async ({ page }) => {
  const state = await installServer(page);
  await passwordLogin(page);
  const historyRequested = deferred();
  const historyReply = deferred();
  const logoutRequested = deferred();
  const logoutReply = deferred();
  await page.route('**/Me/History?*', async route => {
    historyRequested.resolve();
    await historyReply.promise;
    await route.fulfill({ json: { Tracks: [{ ...track, Title: 'Previous private listen' }], Page: 1, TotalPages: 1, Complete: true } });
  });
  await page.route('**/Sessions/Logout', async route => {
    expect(route.request().headers().authorization).toContain(token);
    logoutRequested.resolve();
    await logoutReply.promise;
    await route.fulfill({ status: 204 });
  });
  await page.locator('#history-refresh').click();
  await historyRequested.promise;
  await page.locator('#logout').click();
  await logoutRequested.promise;
  await expect(page.locator('#sign-in')).toBeVisible();
  await expect(page.locator('#history-list')).toBeEmpty();
  expect(await page.evaluate(() => sessionStorage.length)).toBe(0);
  historyReply.resolve();
  logoutReply.resolve();
  await expect(page.locator('#workspace')).toHaveAttribute('aria-busy', 'false');
  await expect(page.locator('#history-list')).toBeEmpty();
  expect(state.disconnected).toBe(false);
});

test('a late history page cannot replace newer navigation', async ({ page }) => {
  await installServer(page);
  await passwordLogin(page);
  const thirdRequested = deferred();
  const thirdReply = deferred();
  const pages = [];
  await page.route('**/Me/History?*', async route => {
    const number = Number(new URL(route.request().url()).searchParams.get('page'));
    pages.push(number);
    if (number === 3) { thirdRequested.resolve(); await thirdReply.promise; }
    await route.fulfill({ json: { Tracks: [{ ...track, Title: `Page ${number} track` }], Page: number, TotalPages: 4, Complete: true, Until: 1788800000 } });
  });
  await page.locator('#history-refresh').click();
  await expect(page.locator('#history-page')).toHaveText('Page 1 of 4');
  await page.locator('#history-next').click();
  await expect(page.locator('#history-page')).toHaveText('Page 2 of 4');
  await page.locator('#history-next').click();
  await thirdRequested.promise;
  await page.locator('#history-previous').click();
  await expect(page.locator('#history-list')).toContainText('Page 2 track');
  thirdReply.resolve();
  await expect(page.locator('#workspace')).toHaveAttribute('aria-busy', 'false');
  await expect(page.locator('#history-page')).toHaveText('Page 2 of 4');
  await expect(page.locator('#history-list')).toContainText('Page 2 track');
  expect(pages).toEqual([1, 2, 3, 2]);
});
