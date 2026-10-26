import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';

const webRoot = new URL('../../src/JellySin.Plugin.Lastfm/Web/', import.meta.url);

function createSettingsHtml({ hash = '#/mypreferencesmenu', userId = 'user-1', includePlayback = true, includeLinks = true } = {}) {
  const playbackLink = includePlayback ? `
    <a class="lnkPlaybackPreferences listItem-border emby-button" style="display:block;margin:0;padding:0">
      <div class="listItem">
        <span class="material-icons listItemIcon listItemIcon-transparent play_circle_filled"></span>
        <div class="listItemBody"><div class="listItemBodyText">Playback</div></div>
      </div>
    </a>` : '';

  const profileLink = includeLinks ? `
    <a class="lnkUserProfile listItem-border emby-button" style="display:block;margin:0;padding:0">
      <div class="listItem">
        <span class="material-icons listItemIcon listItemIcon-transparent person"></span>
        <div class="listItemBody"><div class="listItemBodyText">Profile</div></div>
      </div>
    </a>` : '';

  return `<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <script>
    window.location.hash = ${JSON.stringify(hash)};
    window.ApiClient = { getCurrentUserId: () => ${JSON.stringify(userId)} };
  </script>
  <script type="module" src="/JellySin/Lastfm/Assets/entry.js"></script>
</head>
<body class="mainAnimatedPage">
  <div class="libraryPage userPreferencesPage noSecondaryNavPage mainAnimatedPage">
    <div class="padded-left padded-right padded-bottom-page padded-top">
      <div class="readOnlyContent" style="margin:0 auto">
        <div class="verticalSection verticalSection-extrabottompadding">
          <h2 class="sectionTitle headerUsername" style="padding-left:0.25em">Test User</h2>
          ${profileLink}
          ${playbackLink}
        </div>
      </div>
    </div>
  </div>
</body>
</html>`;
}

async function setupPage(page, html) {
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/JellySin/Lastfm/Assets/entry.js') {
      return route.fulfill({
        contentType: 'text/javascript; charset=utf-8',
        body: await readFile(new URL('entry.js', webRoot), 'utf8'),
      });
    }
    if (url.pathname === '/web/index.html' || url.pathname === '/') {
      return route.fulfill({
        contentType: 'text/html; charset=utf-8',
        body: html,
      });
    }
    return route.abort();
  });
  await page.goto('http://127.0.0.1:4177/web/index.html');
}

test('row inserted once on own settings page and positioned after playback preferences', async ({ page }) => {
  const html = createSettingsHtml({ hash: '#/mypreferencesmenu', userId: 'user-1' });
  await setupPage(page, html);

  const row = page.locator('a.lnkJellySinLastfm');
  await expect(row).toHaveCount(1);
  await expect(row).toHaveAttribute('href', '/JellySin/Lastfm/');
  await expect(row.locator('.listItemBodyText')).toHaveText('Last.fm');
  await expect(row.locator('.listItemIcon')).toHaveClass(/music_note/);

  const isAfterPlayback = await page.evaluate(() => {
    const playback = globalThis.document.querySelector('a.lnkPlaybackPreferences');
    return playback?.nextElementSibling?.classList.contains('lnkJellySinLastfm');
  });
  expect(isAfterPlayback).toBe(true);

  // Trigger DOM mutation to ensure observer does not duplicate row
  await page.evaluate(() => {
    const div = globalThis.document.createElement('div');
    div.id = 'mutation-test';
    globalThis.document.body.appendChild(div);
  });
  await expect(row).toHaveCount(1);
});

test('row is not inserted when viewing another user settings page', async ({ page }) => {
  const html = createSettingsHtml({ hash: '#/mypreferencesmenu?userId=other-user', userId: 'user-1' });
  await setupPage(page, html);

  const row = page.locator('a.lnkJellySinLastfm');
  await expect(row).toHaveCount(0);
});

test('warns once when user settings layout is not recognised', async ({ page }) => {
  const warnings = [];
  page.on('console', msg => {
    if (msg.type() === 'warning') warnings.push(msg.text());
  });

  const html = createSettingsHtml({ includePlayback: false, includeLinks: false });
  await setupPage(page, html);

  const expectedWarning = 'JellySin Last.fm: user settings layout not recognised';
  await expect.poll(() => warnings.filter(text => text.includes(expectedWarning)).length).toBe(1);

  // Subsequent DOM mutations must not repeat warning
  await page.evaluate(() => {
    const div = globalThis.document.createElement('div');
    div.id = 'second-mutation';
    globalThis.document.body.appendChild(div);
  });
  await page.waitForFunction(() => globalThis.document.getElementById('second-mutation') !== null);
  await page.evaluate(() => new Promise(resolve => globalThis.requestAnimationFrame(() => resolve())));
  expect(warnings.filter(text => text.includes(expectedWarning))).toHaveLength(1);
});
