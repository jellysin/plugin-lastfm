// The browser half of proxied_sign_in.py: every scenario opens a page of the plugin the way a person does and
// clicks Connect. Reads the run file the Python half wrote; prints one line per scenario and exits 1 on a failure.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { chromium } from '@playwright/test';

const run = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const callback = '/JellySin/Lastfm/Callback';
const settingsPage = '/web/index.html#/configurationpage?name=jellysin-lastfm';
const wait = 30_000;

class Refused extends Error {}
const require = (condition, description) => { if (!condition) throw new Refused(description); };

/** A browser context that reaches only the server under test; the approval page of Last.fm is a stub. */
async function open(browser) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, locale: 'en-US', viewport: { width: 1280, height: 900 } });
  await context.route('**/*', route => {
    const url = new URL(route.request().url());
    if (url.origin === run.published || url.origin === run.direct) return route.continue();
    if (url.origin === 'https://www.last.fm') return route.fulfill({ status: 200, contentType: 'text/html', body: '<!doctype html><title>Stub of the Last.fm approval page</title>' });
    return route.abort();
  });
  return context;
}

/** A page whose buttons are its own, as the settings page is. */
const onPage = page => ({ page, frame: page });

/** The server's own web client, signed in as `account`, with the plugin's dialog open on the user settings page. */
async function signIn(context, origin, account) {
  const credentials = { Servers: [{ Id: run.serverId, Name: 'Test', ManualAddress: origin, manualAddressOnly: true, LastConnectionMode: 2,
    AccessToken: account.token, UserId: account.id, DateLastAccessed: Date.now() }] };
  await context.addInitScript(([stored]) => {
    localStorage.setItem('jellyfin_credentials', stored);
    localStorage.setItem('_deviceId2', 'proxied-sign-in');
  }, [JSON.stringify(credentials)]);
  const page = await context.newPage();
  await page.goto(origin + '/web/index.html#/mypreferencesmenu?lastfm=open');
  const frame = page.frameLocator('iframe[title="Last.fm"]');
  await frame.locator('#connect-lastfm').waitFor({ state: 'visible', timeout: wait });
  await frame.locator('#connection-status', { hasText: 'Connect Last.fm to start.' }).waitFor({ timeout: wait });
  return { page, frame };
}

/** The server's own web client, signed in the way it stores a session, on the plugin's settings page. */
async function settings(context, origin) {
  const credentials = { Servers: [{ Id: run.serverId, Name: 'Test', ManualAddress: origin, manualAddressOnly: true, LastConnectionMode: 2,
    AccessToken: run.admin.token, UserId: run.admin.id, DateLastAccessed: Date.now() }] };
  await context.addInitScript(([stored]) => {
    localStorage.setItem('jellyfin_credentials', stored);
    localStorage.setItem('_deviceId2', 'proxied-sign-in');
  }, [JSON.stringify(credentials)]);
  const page = await context.newPage();
  await page.goto(origin + settingsPage);
  await page.locator('#jellysin-connect').waitFor({ state: 'visible', timeout: wait });
  // A click before the page knows the connection is answered, then overwritten when the page finishes loading.
  const shown = (await page.locator('#jellysin-connection-status').textContent() ?? '').trim();
  require(shown === 'Last.fm is not connected.', `the page offers Connect while it says ${JSON.stringify(shown)}`);
  return page;
}

/** The browser reached the approval page of Last.fm, asked to return to the published address. Prints no key.
 * The button and its status live in `frame`: the plugin's dialog on the user page, the page itself on the settings page. */
async function approval(context, { page, frame }, click, status) {
  await frame.locator(click).click();
  try { await page.waitForURL(url => url.origin === 'https://www.last.fm', { timeout: 15_000 }); }
  catch { throw new Refused('the browser did not reach Last.fm; the page says: ' + JSON.stringify((await frame.locator(status).textContent() ?? '').trim())); }
  const target = new URL(page.url());
  require(target.pathname === '/api/auth/', 'the approval page has another path');
  require(/^[0-9a-f]{32}$/.test(target.searchParams.get('api_key') ?? ''), 'the approval address names no application key');
  const back = new URL(target.searchParams.get('cb') ?? 'about:blank');
  require(back.origin === run.published && back.pathname === callback, `the return address is ${back.origin}${back.pathname}`);
  require((back.searchParams.get('attempt') ?? '').length >= 32, 'the return address names no attempt');
  const cookie = (await context.cookies(run.published)).find(entry => entry.name === '__Host-JellySinLastfmAttempt');
  require(cookie?.secure && cookie.httpOnly && cookie.path === '/', 'the browser holds no host-only secure cookie for the attempt');
  return `https://www.last.fm/api/auth/?api_key=[32 hex]&cb=${back.origin}${back.pathname}?attempt=[${back.searchParams.get('attempt').length} characters]`;
}

/** The status of the answer to the page's next such request, 0 for none. Never rejects: a rejection nobody awaits yet ends the whole run without a result. */
function answered(page, path, method) {
  return page.waitForResponse(response => response.url().endsWith(path) && response.request().method() === method, { timeout: wait })
    .then(response => response.status(), () => 0);
}

/** The page stays where it is and names both addresses, or sends an ordinary user to the administrator. */
async function refusal({ page, frame }, click, status, expected) {
  await frame.locator(click).click();
  await frame.locator(status, { hasText: 'Last.fm sign-in could not start.' }).waitFor({ timeout: 15_000 })
    .catch(error => console.log(`refusal text not shown within 15 s: ${String(error).split('\n')[0]}`));
  const text = (await frame.locator(status).textContent() ?? '').trim();
  require(new URL(page.url()).origin !== 'https://www.last.fm', 'the browser left for Last.fm from an address the return cannot reach');
  for (const part of expected) require(text.includes(part), `the page says ${JSON.stringify(text)}; missing: ${part}`);
  return text;
}

const both = [`The server names ${run.published}${callback} as the return address`, `this page expects ${run.direct}${callback}`];
const scenarios = {
  'published address, user page, ordinary user': async context => {
    const session = await signIn(context, run.published, run.listener);
    return approval(context, session, '#connect-lastfm', '#status');
  },
  'published address, user page, a saved preference': async context => {
    const { page, frame } = await signIn(context, run.published, run.listener);
    // The page offers this section to a connected account only, and this run connects none: nothing here contacts Last.fm.
    // The section is shown by hand, so that the page's own request for a write other than the sign-in passes the proxy.
    await frame.locator('#favourites').evaluate(section => { section.hidden = false; });
    const saved = answered(page, '/Me/Favourites', 'PUT');
    await frame.locator('#favourites-save').click();
    const status = await saved;
    require(status >= 200 && status < 300, `saving a preference answered HTTP ${status}; the page says: ${JSON.stringify((await frame.locator('#status').textContent() ?? '').trim())}`);
    return `PUT Me/Favourites -> ${status}`;
  },
  'published address, settings page, administrator': async context =>
    approval(context, onPage(await settings(context, run.published)), '#jellysin-connect', '#jellysin-connection-status'),
  // The settings page names the status of a refusal; that rests on how the server's web client rejects a request.
  'published address, settings page, a refused request names its status': async context => {
    const page = await settings(context, run.published);
    const rejection = await page.evaluate(() => globalThis.ApiClient.ajax({ type: 'POST', url: globalThis.ApiClient.getUrl('JellySin/Lastfm/Me/Connection/Refusal'),
      contentType: 'application/json', data: JSON.stringify({ Reason: 'made-up' }) }).then(() => 'accepted', error => typeof error?.status === 'number' ? error.status : 'no status'));
    require(rejection === 400, `the web client reports a refused request as: ${rejection}`);
    return 'a made-up reason -> rejected with status 400';
  },
  'second address, user page, administrator': async context => {
    const session = await signIn(context, run.direct, run.admin);
    return refusal(session, '#connect-lastfm', '#status', both);
  },
  'second address, user page, ordinary user': async context => {
    const session = await signIn(context, run.direct, run.listener);
    return refusal(session, '#connect-lastfm', '#status', ['Ask the administrator of this server to check the server address setting.']);
  },
  'second address, settings page, administrator': async context =>
    refusal(onPage(await settings(context, run.direct)), '#jellysin-connect', '#jellysin-connection-status', both),
};

const browser = await chromium.launch({ args: [`--host-resolver-rules=MAP ${run.host}:443 127.0.0.1:${run.proxyPort}`] });
const results = [];
try {
  for (const [name, scenario] of Object.entries(scenarios).filter(([name]) => name.includes(run.only))) {
    const context = await open(browser);
    try { results.push({ name, passed: true, detail: await scenario(context) }); }
    catch (error) {
      results.push({ name, passed: false, detail: error instanceof Refused ? error.message : String(error).split('\n')[0] });
      for (const [index, page] of context.pages().entries()) {
        await page.screenshot({ path: join(run.report, `${name.replaceAll(/[^a-z]+/g, '-')}-${index}.png`) })
          .catch(error => console.log(`screenshot skipped: ${String(error).split('\n')[0]}`));
      }
    }
    await context.close();
    const last = results.at(-1);
    console.log(`${last.passed ? 'PASS' : 'FAIL'}: ${last.name}: ${last.detail}`);
  }
} finally { await browser.close(); }
writeFileSync(join(run.report, 'result.json'), JSON.stringify(results, null, 1) + '\n');
process.exit(results.length > 0 && results.every(result => result.passed) ? 0 : 1);
