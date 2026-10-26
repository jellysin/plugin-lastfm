// The browser half of page_check.py: opens each page of the plugin the way a person does, saves one screenshot per
// page into the evidence directory and prints one line per check. Exits 1 on a failure or on any page error.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { chromium } from '@playwright/test';

const run = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const settingsPage = '/web/index.html#/configurationpage?name=jellysin-lastfm';
const wait = 30_000;
const approvalToken = 'stand-in-approval-token';

class Refused extends Error {}
const require = (condition, description) => { if (!condition) throw new Refused(description); };
const text = async (target, selector) => ((await target.locator(selector).textContent()) ?? '').trim();
const shot = (target, name, selector) => (selector ? target.locator(selector) : target).screenshot({ path: join(run.evidence, name + '.png') });

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

/** Writes stored web client credentials for an account and opens a page on that session. */
async function session(context, account) {
  const credentials = { Servers: [{ Id: run.serverId, Name: 'Test', ManualAddress: run.origin, manualAddressOnly: true, LastConnectionMode: 2,
    AccessToken: account.token, UserId: account.id, DateLastAccessed: Date.now() }] };
  await context.addInitScript(([stored, origin]) => {
    if (location.origin !== origin) return;
    localStorage.setItem('jellyfin_credentials', stored);
    localStorage.setItem('_deviceId2', 'page-check');
  }, [JSON.stringify(credentials), run.origin]);
  return context.newPage();
}

/** The server's own web client, signed in the way it stores a session, on the plugin's settings page. */
async function settings(context) {
  const page = await session(context, run.admin);
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
    const page = await session(context, run.listener);
    await page.goto(run.origin + '/web/index.html#/mypreferencesmenu?lastfm=open');
    let frame = page.frameLocator('iframe[title="Last.fm"]');
    await frame.locator('#connect-lastfm').waitFor({ state: 'visible', timeout: wait });
    const callback = page.waitForResponse(response => new URL(response.url()).pathname.endsWith('/JellySin/Lastfm/Callback'), { timeout: wait });
    await frame.locator('#connect-lastfm').click();
    await callback;
    frame = page.frameLocator('iframe[title="Last.fm"]');
    await frame.locator('#connection-status', { hasText: `Connected as ${run.lastfmUser}` }).waitFor({ timeout: wait })
      .catch(async () => { throw new Refused(`the page says ${JSON.stringify(await text(frame, '#connection-status'))}; status ${JSON.stringify(await text(frame, '#status'))}`); });
    await shot(page, '2-connected');
    run.page = page;
    run.frame = () => page.frameLocator('iframe[title="Last.fm"]');
    return await text(frame, '#connection-status');
  },
  'charts': async () => {
    const frame = run.frame();
    await frame.locator('#charts-refresh').click();
    await frame.locator('#chart-artists', { hasText: run.artist }).waitFor({ timeout: wait });
    for (const id of ['#chart-tracks', '#chart-albums']) require((await text(frame, id)).length > 0, `${id} is empty`);
    await shot(frame, '3-charts', '#overview');
    return `tracks: ${JSON.stringify((await text(frame, '#chart-tracks')).slice(0, 80))}`;
  },
  'recent listens': async () => {
    const frame = run.frame();
    await frame.locator('#history-refresh').click();
    await frame.locator('#history-list', { hasText: run.seed }).waitFor({ timeout: wait });
    await shot(frame, '4-history', '#history');
    return JSON.stringify((await text(frame, '#history-list')).slice(0, 80));
  },
  'one discovery result': async () => {
    const frame = run.frame();
    await frame.locator('#discovery-search').fill(run.seed);
    await frame.locator('#discovery-search-button').click();
    await frame.locator(`#discovery-seed option[value="${run.seedItemId}"]`).waitFor({ state: 'attached', timeout: wait });
    await frame.locator('#discovery-seed').selectOption(run.seedItemId);
    await frame.locator('#discovery-refresh').click();
    await frame.locator('#discovery-external', { hasText: run.similarRemote }).waitFor({ timeout: wait });
    await frame.locator('#discovery-local', { hasText: run.similarLocal }).waitFor({ timeout: wait });
    await shot(frame, '5-discovery', '#discovery');
    return `in library: ${JSON.stringify((await text(frame, '#discovery-local')).slice(0, 60))}; beyond: ${JSON.stringify((await text(frame, '#discovery-external')).slice(0, 60))}`;
  },
  'account settings': async () => {
    const frame = run.frame();
    for (const id of ['#scrobbling', '#library-exclusions', '#device-exclusions', '#media-type-exclusions', '#scrobble-log']) {
      require(await frame.locator(id).isVisible(), `${id} is not visible`);
    }
    for (const id of ['#library-exclusions-summary', '#device-exclusions-summary', '#media-type-exclusions-summary']) {
      await frame.locator(id).filter({ hasNotText: /^Loading/ }).waitFor({ timeout: wait });
    }
    await shot(frame, '6-account-settings', '#connection');
    const lib = (await text(frame, '#library-exclusions-summary')).slice(0, 60);
    const dev = (await text(frame, '#device-exclusions-summary')).slice(0, 60);
    const med = (await text(frame, '#media-type-exclusions-summary')).slice(0, 60);
    return `libraries: ${JSON.stringify(lib)}; devices: ${JSON.stringify(dev)}; media types: ${JSON.stringify(med)}`;
  },
  'delivery status': async () => {
    const frame = run.frame();
    const details = frame.locator('#connection details', { hasText: 'Delivery status' });
    if (!(await details.evaluate(el => el.open))) await details.locator('summary').click();
    await frame.locator('#delivery-refresh').click();
    await frame.locator('#delivery-status').filter({ hasNotText: 'Refresh to inspect pending submissions.' }).waitFor({ timeout: wait });
    await shot(details, '7-delivery-status');
    return JSON.stringify((await text(frame, '#delivery-status')).slice(0, 80));
  },
  'import preview': async () => {
    const frame = run.frame();
    const details = frame.locator('#history details', { hasText: 'Import play counts into Jellyfin' });
    if (!(await details.evaluate(el => el.open))) await details.locator('summary').click();
    await frame.locator('#history-preview').click();
    await frame.locator('#history-preview-list p').first().waitFor({ state: 'visible', timeout: wait });
    await shot(details, '8-import-preview');
    return JSON.stringify((await text(frame, '#history-preview-list p:first-child')).slice(0, 80));
  },
  'disconnect confirmation': async () => {
    const frame = run.frame();
    await frame.locator('#disconnect-lastfm').click();
    await frame.locator('#disconnect-confirmation').waitFor({ state: 'visible', timeout: wait });
    await shot(frame, '9-disconnect-confirmation', '#disconnect-confirmation');
    await frame.locator('#cancel-disconnect').click();
    await frame.locator('#disconnect-confirmation').waitFor({ state: 'hidden', timeout: wait });
    require(await frame.locator('#disconnect-confirmation').isHidden(), '#disconnect-confirmation is still visible');
    const status = await text(frame, '#connection-status');
    require(status === `Connected as ${run.lastfmUser}`, `the page says ${JSON.stringify(status)}`);
    return status;
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
