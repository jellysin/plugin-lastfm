// The browser half of settings_entry.py: verifies the Last.fm settings row and dialog in Jellyfin web.
// Exits 1 on a failure or on any page error.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { chromium } from '@playwright/test';

const run = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const wait = 30_000;

class Refused extends Error {}
const require = (condition, description) => { if (!condition) throw new Refused(description); };

// The planted layout change: renames the classes the entry script anchors on, as a web release that moves them would.
function moveLayout() {
  const map = {
    lnkPlaybackPreferences: 'lnkMovedPreferences',
    verticalSection: 'movedSection',
    'listItem-border': 'movedItem',
  };
  const renameElement = el => {
    if (!el || el.nodeType !== 1) return;
    for (const [from, to] of Object.entries(map)) {
      if (el.classList?.contains(from)) {
        el.classList.remove(from);
        el.classList.add(to);
      }
    }
  };
  const renameSubtree = root => {
    if (!root) return;
    renameElement(root);
    if (root.getElementsByClassName) {
      for (const from of Object.keys(map)) {
        for (const el of Array.from(root.getElementsByClassName(from))) {
          renameElement(el);
        }
      }
    }
  };
  const observer = new MutationObserver(mutations => {
    for (const mutation of mutations) {
      if (mutation.type === 'childList') {
        for (const node of mutation.addedNodes) {
          renameSubtree(node);
        }
      } else if (mutation.type === 'attributes' && mutation.attributeName === 'class') {
        renameElement(mutation.target);
      }
    }
  });
  observer.observe(document, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
  renameSubtree(document.documentElement || document);
}

async function open(browser, errors) {
  const context = await browser.newContext({ locale: 'en-US', viewport: { width: 1280, height: 900 } });
  await context.route('**/*', route => {
    const url = new URL(route.request().url());
    if (url.origin === run.origin) return route.continue();
    return route.abort();
  });
  context.on('page', page => {
    page.on('pageerror', error => errors.push(`${page.url()}: ${String(error).split('\n')[0]}`));
    page.on('console', message => {
      const source = message.location().url ?? '';
      const foreign = source.length > 0 && !source.startsWith(run.origin);
      if (message.type() === 'error' && !(foreign && message.text().startsWith('Failed to load resource'))) {
        errors.push(`${page.url()}: console: ${message.text().slice(0, 200)} (${source})`);
      }
    });
  });
  if (run.plant === 'moved-layout') await context.addInitScript(moveLayout);
  return context;
}

async function session(context, account) {
  const credentials = {
    Servers: [{
      Id: run.serverId,
      Name: 'Test',
      ManualAddress: run.origin,
      manualAddressOnly: true,
      LastConnectionMode: 2,
      AccessToken: account.token,
      UserId: account.id,
      DateLastAccessed: Date.now(),
    }],
  };
  await context.addInitScript(([stored, origin]) => {
    if (location.origin !== origin) return;
    localStorage.setItem('jellyfin_credentials', stored);
    localStorage.setItem('_deviceId2', 'settings-entry-check');
  }, [JSON.stringify(credentials), run.origin]);
  return context.newPage();
}

let activePage = null;

async function probeDialogGeometry(page) {
  return page.evaluate(() => {
    const dialog = document.querySelector('.jellysin-lastfm-dialog');
    if (!dialog) return { ok: false, headerBottom: 0, dialogTop: 0, reason: 'dialog missing' };

    const dialogTop = dialog.getBoundingClientRect().top;
    const headers = document.querySelectorAll('.skinHeader, .MuiAppBar-root');
    let maxHeaderBottom = 0;
    const count = headers.length;
    for (let i = 0; i < count; i++) {
      const header = headers[i];
      if (window.getComputedStyle(header).display === 'none') continue;
      const rect = header.getBoundingClientRect();
      if (rect.height <= 0) continue;
      if (rect.bottom > maxHeaderBottom) {
        maxHeaderBottom = rect.bottom;
      }
    }

    if (dialogTop < maxHeaderBottom) {
      return {
        ok: false,
        headerBottom: maxHeaderBottom,
        dialogTop,
        reason: `dialog top (${dialogTop}px) < header bottom (${maxHeaderBottom}px)`,
      };
    }

    const closeBtn = document.querySelector('.jellysin-lastfm-close');
    if (!closeBtn) {
      return { ok: false, headerBottom: maxHeaderBottom, dialogTop, reason: 'close button missing' };
    }

    const rect = closeBtn.getBoundingClientRect();
    const cx = rect.left + rect.width / 2;
    const cy = rect.top + rect.height / 2;
    const hit = document.elementFromPoint(cx, cy);
    const hitOk = hit === closeBtn || closeBtn.contains(hit);
    if (!hitOk) {
      return {
        ok: false,
        headerBottom: maxHeaderBottom,
        dialogTop,
        reason: `close button covered at (${cx}, ${cy})`,
      };
    }

    return { ok: true, headerBottom: maxHeaderBottom, dialogTop, reason: '' };
  });
}

const checks = {
  'Last.fm row in user settings': async context => {
    const page = await session(context, run.listener);
    activePage = page;
    await page.goto(run.origin + '/web/index.html#/mypreferencesmenu');
    const row = page.locator('a.lnkJellySinLastfm');
    await row.waitFor({ state: 'visible', timeout: wait });
    for (const selector of ['#loginPage', '.manualLoginForm']) {
      const visible = await page.locator(selector).isVisible().catch(() => false);
      require(!visible, `login form ${selector} is visible`);
    }
    await page.screenshot({ path: join(run.evidence, '1-user-settings.png'), fullPage: true });
    const rowText = (await row.textContent() ?? '').trim();
    const prevClass = await row.evaluate(el => el.previousElementSibling?.className ?? '');
    return `${rowText} (preceding: ${prevClass})`;
  },
  'Last.fm page opens without a sign-in': async () => {
    require(activePage !== null, 'No active page from user settings check');
    await activePage.locator('a.lnkJellySinLastfm').click();
    const frame = activePage.frameLocator('iframe[title="Last.fm"]');
    await frame.locator('#connect-lastfm').waitFor({ state: 'visible', timeout: wait });
    const passwordVisible = await frame.locator('input[type="password"]').isVisible().catch(() => false);
    require(!passwordVisible, 'Last.fm frame has a visible password input');
    const geometry = await probeDialogGeometry(activePage);
    require(geometry.ok, `Dialog geometry invalid: ${geometry.reason}`);
    await activePage.screenshot({ path: join(run.evidence, '2-lastfm-dialog.png') });
    const connectText = (await frame.locator('#connect-lastfm').textContent() ?? '').trim();
    return `${connectText} (header bottom: ${geometry.headerBottom}px, dialog top: ${geometry.dialogTop}px)`;
  },
  'Last.fm page reopens below the header after the sign-in return': async () => {
    require(activePage !== null, 'No active page from user settings check');
    await activePage.keyboard.press('Escape');
    await activePage.locator('.jellysin-lastfm-dialog').waitFor({ state: 'detached', timeout: wait });
    // A hash-only change would not reload the web client; the sign-in return is a fresh load.
    await activePage.goto('about:blank');
    await activePage.goto(run.origin + '/web/index.html#/mypreferencesmenu?lastfm=open');
    const frame = activePage.frameLocator('iframe[title="Last.fm"]');
    await frame.locator('#connect-lastfm').waitFor({ state: 'visible', timeout: wait });
    const geometry = await probeDialogGeometry(activePage);
    require(geometry.ok, `Dialog geometry invalid: ${geometry.reason}`);
    await activePage.screenshot({ path: join(run.evidence, '3-lastfm-return.png') });
    const connectText = (await frame.locator('#connect-lastfm').textContent() ?? '').trim();
    return `${connectText} (header bottom: ${geometry.headerBottom}px, dialog top: ${geometry.dialogTop}px)`;
  },
};

const browser = await chromium.launch();
const results = [];
const errors = [];
try {
  const context = await open(browser, errors);
  for (const [name, check] of Object.entries(checks)) {
    try {
      results.push({ name, passed: true, detail: await check(context) });
    } catch (error) {
      results.push({
        name,
        passed: false,
        detail: error instanceof Refused ? error.message : String(error).split('\n')[0],
      });
      if (activePage) {
        await activePage.screenshot({ path: join(run.evidence, `failed-${name.replaceAll(/[^a-z]+/g, '-')}.png`) })
          .catch(err => console.log(`screenshot skipped: ${String(err).split('\n')[0]}`));
      }
    }
    const last = results.at(-1);
    console.log(`${last.passed ? 'PASS' : 'FAIL'}: ${last.name}: ${last.detail}`);
    if (!last.passed) break;
  }
} finally {
  await browser.close();
}

for (const error of errors) console.log(`PAGE ERROR: ${error}`);
writeFileSync(join(run.evidence, 'result.json'), JSON.stringify({
  server: run.version,
  plant: run.plant,
  results,
  errors,
}, null, 2) + '\n');
const allPassed = results.length === Object.keys(checks).length && results.every(r => r.passed) && errors.length === 0;
process.exit(allPassed ? 0 : 1);
