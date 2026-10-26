import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';

const webRoot = new URL('../../src/JellySin.Plugin.Lastfm/Web/', import.meta.url);

function createSettingsHtml({ hash = '#/mypreferencesmenu', userId = 'user-1', includePlayback = true, includeLinks = true, header = 'skin' } = {}) {
  let headerHtml = '';
  if (header === 'skin') {
    headerHtml = '<div class="skinHeader" style="height:60px"></div>';
  } else if (header === 'appbar') {
    headerHtml = '<header class="MuiAppBar-root" style="position:fixed;top:0;left:0;right:0;height:64px;z-index:1100"></header>';
  }
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
<body class="mainAnimatedPage" style="margin:0">
  ${headerHtml}
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

test('click row opens dialog with iframe src containing /JellySin/Lastfm/?embedded=1 and close button', async ({ page }) => {
  const html = createSettingsHtml({ hash: '#/mypreferencesmenu', userId: 'user-1' });
  await setupPage(page, html);

  const row = page.locator('a.lnkJellySinLastfm');
  await expect(row).toBeVisible();
  await row.click();

  const dialog = page.locator('div[role="dialog"][aria-label="Last.fm"]');
  await expect(dialog).toBeVisible();
  const iframe = dialog.locator('iframe[title="Last.fm"]');
  await expect(iframe).toHaveAttribute('src', '/JellySin/Lastfm/?embedded=1');
  const closeBtn = dialog.locator('button[aria-label="Close"]');
  await expect(closeBtn).toBeVisible();
  await expect(closeBtn).toHaveText('×');

  await closeBtn.click();
  await expect(dialog).toHaveCount(0);
});

test('Escape closes the opened in-client dialog', async ({ page }) => {
  const html = createSettingsHtml({ hash: '#/mypreferencesmenu', userId: 'user-1' });
  await setupPage(page, html);

  await page.locator('a.lnkJellySinLastfm').click();
  const dialog = page.locator('div[role="dialog"][aria-label="Last.fm"]');
  await expect(dialog).toBeVisible();

  await page.keyboard.press('Escape');
  await expect(dialog).toHaveCount(0);
});

test('auto-open from #/mypreferencesmenu with lastfm=open and connection=failed forwards to iframe and strips hash', async ({ page }) => {
  const html = createSettingsHtml({ hash: '#/mypreferencesmenu?lastfm=open&connection=failed', userId: 'user-1' });
  await setupPage(page, html);

  const dialog = page.locator('div[role="dialog"][aria-label="Last.fm"]');
  await expect(dialog).toBeVisible();
  const iframe = dialog.locator('iframe[title="Last.fm"]');
  await expect(iframe).toHaveAttribute('src', '/JellySin/Lastfm/?embedded=1&connection=failed');

  const currentHash = await page.evaluate(() => globalThis.location.hash);
  expect(currentHash).toBe('#/mypreferencesmenu');
});

test('no dialog on other user settings page even with lastfm=open', async ({ page }) => {
  const html = createSettingsHtml({ hash: '#/mypreferencesmenu?userId=other-user&lastfm=open&connection=failed', userId: 'user-1' });
  await setupPage(page, html);

  const dialog = page.locator('div[role="dialog"][aria-label="Last.fm"]');
  await expect(dialog).toHaveCount(0);
  const row = page.locator('a.lnkJellySinLastfm');
  await expect(row).toHaveCount(0);
});

test('auto-open without header re-places dialog below header when header is inserted later', async ({ page }) => {
  const html = createSettingsHtml({ hash: '#/mypreferencesmenu?lastfm=open', header: 'none' });
  await setupPage(page, html);

  const dialog = page.locator('div[role="dialog"][aria-label="Last.fm"]');
  await expect(dialog).toBeVisible();

  const initialBox = await dialog.boundingBox();
  expect(initialBox?.y).toBe(0);

  await page.evaluate(() => {
    const header = globalThis.document.createElement('div');
    header.className = 'skinHeader';
    header.style.height = '60px';
    globalThis.document.body.prepend(header);
  });

  await expect.poll(async () => {
    const box = await dialog.boundingBox();
    return box?.y;
  }).toBe(60);

  const geometry = await page.evaluate(() => {
    const dialogEl = globalThis.document.querySelector('.jellysin-lastfm-dialog');
    const headerEl = globalThis.document.querySelector('.skinHeader');
    return {
      dialogTop: dialogEl?.getBoundingClientRect().top,
      headerBottom: headerEl?.getBoundingClientRect().bottom,
    };
  });
  expect(geometry.headerBottom).toBe(60);
  expect(geometry.dialogTop).toBe(60);
  await expect(dialog.locator('button[aria-label="Close"]')).toBeVisible();

  const hitMatchesClose = await page.evaluate(() => {
    const btn = globalThis.document.querySelector('.jellysin-lastfm-close');
    if (!btn) return false;
    const rect = btn.getBoundingClientRect();
    const hit = globalThis.document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
    return hit === btn || btn.contains(hit);
  });
  expect(hitMatchesClose).toBe(true);
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

for (const [header, height] of [['appbar', 64], ['skin', 60]]) {
  test(`dialog is positioned below ${header} header and close button is not covered`, async ({ page }) => {
    await setupPage(page, createSettingsHtml({ header }));

    const row = page.locator('a.lnkJellySinLastfm');
    await expect(row).toBeVisible();
    await row.click();

    const dialog = page.locator('div[role="dialog"][aria-label="Last.fm"]');
    await expect(dialog).toBeVisible();
    const box = await dialog.boundingBox();
    expect(box.y).toBeGreaterThanOrEqual(height);
    await expect(dialog.locator('button[aria-label="Close"]')).toBeVisible();

    const hitMatchesClose = await page.evaluate(() => {
      const btn = globalThis.document.querySelector('.jellysin-lastfm-close');
      if (!btn) return false;
      const rect = btn.getBoundingClientRect();
      const hit = globalThis.document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
      return hit === btn || btn.contains(hit);
    });
    expect(hitMatchesClose).toBe(true);
  });
}
