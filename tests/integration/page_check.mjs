// The browser half of page_check.py: opens each page of the plugin the way a person does, saves one screenshot per
// page into the evidence directory and prints one line per check. Exits 1 on a failure or on any page error.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { chromium } from '@playwright/test';

const run = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const settingsPage = '/web/index.html#/configurationpage?name=jellysin-lastfm';
const userPage = run.prefix + '/';
const wait = 30_000;
const approvalToken = 'stand-in-approval-token';

class Refused extends Error {}
const require = (condition, description) => { if (!condition) throw new Refused(description); };
const text = async (page, selector) => ((await page.locator(selector).textContent()) ?? '').trim();
const shot = (page, name, selector) => (selector ? page.locator(selector) : page).screenshot({ path: join(run.evidence, name + '.png') });

/** A context that reaches only the server under test. Last.fm's approval page approves at once and returns to the callback. */
async function open(browser, errors) {
  const context = await browser.newContext({ locale: 'en-US', viewport: { width: 1280, height: 900 } });
  await context.route('**/*', route => {
    const url = new URL(route.request().url());
    if (url.origin === run.origin) return route.continue();
    if (url.origin === 'https://www.last.fm' && url.pathname === '/api/auth/') {
      const back = new URL(url.searchParams.get('cb') ?? 'about:blank');
      if (back.origin !== run.origin) return route.abort();
      back.searchParams.set('token', approvalToken);
      return route.fulfill({ status: 302, headers: { location: back.href } });
    }
    return route.abort();
  });
  context.on('page', page => {
    page.on('pageerror', error => errors.push(`${page.url()}: ${String(error).split('\n')[0]}`));
    // A request this context aborts on purpose (anything not on the server) is logged as a failed load; that is no page error.
    page.on('console', message => {
      const source = message.location().url ?? '';
      const foreign = source.length > 0 && !source.startsWith(run.origin);
      if (message.type() === 'error' && !(foreign && message.text().startsWith('Failed to load resource'))) errors.push(`${page.url()}: console: ${message.text().slice(0, 200)} (${source})`);
    });
  });
  return context;
}

/** The server's own web client, signed in the way it stores a session, on the plugin's settings page. */
async function settings(context) {
  const credentials = { Servers: [{ Id: run.serverId, Name: 'Test', ManualAddress: run.origin, manualAddressOnly: true, LastConnectionMode: 2,
    AccessToken: run.admin.token, UserId: run.admin.id, DateLastAccessed: Date.now() }] };
  await context.addInitScript(([stored, origin]) => {
    if (location.origin !== origin) return;
    localStorage.setItem('jellyfin_credentials', stored);
    localStorage.setItem('_deviceId2', 'page-check');
  }, [JSON.stringify(credentials), run.origin]);
  const page = await context.newPage();
  await page.goto(run.origin + settingsPage);
  await page.locator('#jellysin-settings').waitFor({ state: 'visible', timeout: wait });
  return page;
}

async function saved(page, path, method, click) {
  const response = page.waitForResponse(answer => new URL(answer.url()).pathname.endsWith(path) && answer.request().method() === method, { timeout: wait });
  await page.click(click);
  return (await response).status();
}

const checks = {
  'administrator settings page': async context => {
    const page = await settings(context);
    // The page fills the settings first and the application status after them; once the status shows, both are loaded.
    await page.locator('#jellysin-application-status').filter({ hasText: /\S/ }).waitFor({ timeout: wait });
    require(await page.locator('#jellysin-enabled').isChecked(), 'the plugin is not shown as enabled');
    const settingsStatus = await saved(page, '/Plugins/2034650d-a290-4a16-b195-89fb44cfb932/Configuration', 'POST', '#jellysin-settings button[type=submit]');
    require(settingsStatus >= 200 && settingsStatus < 300, `saving the settings answered HTTP ${settingsStatus}`);
    // With no application configured the page opens this section itself; a click would close it again.
    if (!(await page.locator('#jellysin-advanced').evaluate(details => details.open))) await page.click('#jellysin-advanced summary');
    await page.fill('#jellysin-api-key', 'a'.repeat(32));
    await page.fill('#jellysin-api-secret', 'b'.repeat(32));
    const applicationStatus = await saved(page, '/JellySin/Lastfm/Admin/Application', 'PUT', '#jellysin-application-form button[type=submit]');
    require(applicationStatus >= 200 && applicationStatus < 300, `saving the application answered HTTP ${applicationStatus}`);
    await page.locator('#jellysin-remove-override').waitFor({ state: 'visible', timeout: wait });
    // The form re-enables its button once the account section below has been refreshed for the new application.
    await page.waitForFunction(() => !document.querySelector('#jellysin-application-form button[type=submit]').disabled, null, { timeout: wait });
    const account = await text(page, '#jellysin-connection-status');
    require(!account.startsWith('Add a Last.fm application'), `the account section still asks for an application: ${JSON.stringify(account)}`);
    await shot(page, '1-admin-settings');
    return `settings saved (${settingsStatus}), application saved (${applicationStatus}); status: ${JSON.stringify(await text(page, '#jellysin-application-status'))}; account: ${JSON.stringify(account)}`;
  },
  'ordinary user connects through the stand-in': async context => {
    const page = await context.newPage();
    await page.goto(run.origin + userPage);
    await page.click('#sign-in summary');
    await page.fill('#username', run.listener.name);
    await page.fill('#password', run.listener.password);
    await page.click('#login-submit');
    await page.locator('#connect-lastfm').waitFor({ state: 'visible', timeout: wait });
    await page.click('#connect-lastfm');
    await page.locator('#connection-status', { hasText: `Connected as ${run.lastfmUser}` }).waitFor({ timeout: wait })
      .catch(async () => { throw new Refused(`the page says ${JSON.stringify(await text(page, '#connection-status'))}; status ${JSON.stringify(await text(page, '#status'))}`); });
    await shot(page, '2-connected');
    run.page = page;
    return await text(page, '#connection-status');
  },
  'charts': async () => {
    const page = run.page;
    await page.click('#charts-refresh');
    await page.locator('#chart-artists', { hasText: run.artist }).waitFor({ timeout: wait });
    for (const id of ['#chart-tracks', '#chart-albums']) require((await text(page, id)).length > 0, `${id} is empty`);
    await shot(page, '3-charts', '#overview');
    return `tracks: ${JSON.stringify((await text(page, '#chart-tracks')).slice(0, 80))}`;
  },
  'recent listens': async () => {
    const page = run.page;
    await page.click('#history-refresh');
    await page.locator('#history-list', { hasText: run.seed }).waitFor({ timeout: wait });
    await shot(page, '4-history', '#history');
    return JSON.stringify((await text(page, '#history-list')).slice(0, 80));
  },
  'one discovery result': async () => {
    const page = run.page;
    await page.fill('#discovery-search', run.seed);
    await page.click('#discovery-search-button');
    await page.locator(`#discovery-seed option[value="${run.seedItemId}"]`).waitFor({ state: 'attached', timeout: wait });
    await page.selectOption('#discovery-seed', run.seedItemId);
    await page.click('#discovery-refresh');
    await page.locator('#discovery-external', { hasText: run.similarRemote }).waitFor({ timeout: wait });
    await page.locator('#discovery-local', { hasText: run.similarLocal }).waitFor({ timeout: wait });
    await shot(page, '5-discovery', '#discovery');
    return `in library: ${JSON.stringify((await text(page, '#discovery-local')).slice(0, 60))}; beyond: ${JSON.stringify((await text(page, '#discovery-external')).slice(0, 60))}`;
  },
};

const browser = await chromium.launch();
const results = [];
const errors = [];
try {
  const admin = await open(browser, errors);
  const user = await open(browser, errors);
  for (const [name, check] of Object.entries(checks)) {
    const context = name.startsWith('administrator') ? admin : user;
    try { results.push({ name, passed: true, detail: await check(context) }); }
    catch (error) {
      results.push({ name, passed: false, detail: error instanceof Refused ? error.message : String(error).split('\n')[0] });
      for (const [index, page] of context.pages().entries()) await shot(page, `failed-${name.replaceAll(/[^a-z]+/g, '-')}-${index}`).catch(shotError => console.log(`screenshot skipped: ${String(shotError).split('\n')[0]}`));
    }
    const last = results.at(-1);
    console.log(`${last.passed ? 'PASS' : 'FAIL'}: ${last.name}: ${last.detail}`);
    if (!last.passed) break;
  }
} finally { await browser.close(); }
for (const error of errors) console.log(`PAGE ERROR: ${error}`);
writeFileSync(join(run.evidence, 'result.json'), JSON.stringify({ server: run.version, results, errors }, null, 1) + '\n');
process.exit(results.length === Object.keys(checks).length && results.every(result => result.passed) && errors.length === 0 ? 0 : 1);
