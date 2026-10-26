import { test, expect } from '@playwright/test';
import { installServer, passwordLogin, track } from './fixture.mjs';

function deferred() {
  let resolve;
  const promise = new Promise(complete => { resolve = complete; });
  return { promise, resolve };
}

test('session expiration clears private views before a delayed request finishes', async ({ page }) => {
  const state = await installServer(page);
  await passwordLogin(page);
  const historyRequested = deferred();
  const historyReply = deferred();
  await page.route('**/Me/History?*', async route => {
    historyRequested.resolve();
    await historyReply.promise;
    await route.fulfill({ json: { Tracks: [{ ...track, Title: 'Previous private listen' }], Page: 1, TotalPages: 1, Complete: true } });
  });
  await page.locator('#history-refresh').click();
  await historyRequested.promise;
  await page.route('**/Me/Favourites*', route => route.fulfill({ status: 401, body: 'Session expired' }));
  await page.locator('#favourites-refresh').click();
  await expect(page.locator('#open-in-jellyfin')).toBeVisible();
  await expect(page.locator('#history-list')).toBeEmpty();
  historyReply.resolve();
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
