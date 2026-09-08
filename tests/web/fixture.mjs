import { readFile } from 'node:fs/promises';

export const token = 'a'.repeat(32);
export const track = { Artist: 'Massive Attack', Title: 'Teardrop', Album: 'Mezzanine', PlayCount: 42, Url: 'https://www.last.fm/music/Massive+Attack/_/Teardrop', NowPlaying: false };
const item = '11111111-1111-4111-8111-111111111111';
const previewId = '22222222-2222-4222-8222-222222222222';
const json = body => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
const empty = () => ({ status: 204 });
const webRoot = new URL('../../src/JellySin.Plugin.Lastfm/Web/', import.meta.url);

export async function installServer(page, { prefix = '', administrator = false, latencyMs = 0 } = {}) {
  const state = { requests: [], prefix, administrator, connected: true, favourites: false, removed: false, recipes: [], imported: false, preview: false, disconnected: false,
    historyTracks: [track], delivery: { Pending: 3, Blocked: 1, LegacyPluginDetected: false, DroppedSnapshots: 0, FailedWrites: 0, Rejected: 0, PendingPersistence: 0 } };
  await page.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.origin !== 'http://127.0.0.1:4177' || !url.pathname.startsWith(prefix + '/')) return route.abort();
    const path = url.pathname.slice(prefix.length);
    if (latencyMs) await new Promise(resolve => setTimeout(resolve, latencyMs));
    state.requests.push({ path, url: url.href, method: request.method(), headers: request.headers(), body: request.postDataJSON() });
    if (path === '/JellySin/Lastfm/') return route.fulfill({ contentType: 'text/html', body: await readFile(new URL('index.html', webRoot), 'utf8') });
    if (path.startsWith('/JellySin/Lastfm/Assets/')) {
      const name = path.split('/').at(-1);
      if (!/^[a-z-]+\.(js|css)$/.test(name)) return route.abort();
      return route.fulfill({ contentType: name.endsWith('.js') ? 'text/javascript' : 'text/css', body: await readFile(new URL(name, webRoot), 'utf8') });
    }
    const response = respond(path, request.method(), request.postDataJSON(), url, state);
    return route.fulfill(response);
  });
  await page.goto(`http://127.0.0.1:4177${prefix}/JellySin/Lastfm/`);
  return state;
}

function respond(path, method, body, url, state) {
  if (path === '/JellySin/Lastfm/Bootstrap') return json({ BasePath: state.prefix, ServerId: 'fixture-server', Version: '1.0.0' });
  if (path === '/Users/AuthenticateByName' || path === '/Users/AuthenticateWithQuickConnect') return json({ AccessToken: token, ServerId: 'fixture-server', User: { Name: 'listener' } });
  if (path === '/QuickConnect/Enabled') return json(true);
  if (path === '/QuickConnect/Initiate') return json({ Secret: 'q'.repeat(32), Code: '123456' });
  if (path === '/JellySin/Lastfm/Auth/QuickConnect/Status') return json({ Authenticated: true });
  if (path === '/Sessions/Logout') return empty();
  const endpoint = path.replace('/JellySin/Lastfm/', '');
  if (endpoint === 'Me') return json({ UserName: 'listener', IsAdministrator: state.administrator, Connection: { Connected: state.connected, Username: 'Listener', ScrobblingEnabled: true } });
  if (endpoint === 'Admin/Application') return method === 'GET' ? json({ Configured: true }) : empty();
  if (endpoint === 'Me/Connection/Begin') return json({ AttemptId: previewId, AuthorizationUrl: 'https://www.last.fm/api/auth/?api_key=fixture&token=fixture' });
  if (endpoint === 'Me/Connection/Finish') { state.connected = true; return empty(); }
  if (endpoint === 'Me/Connection' && method === 'DELETE') { state.connected = false; state.disconnected = true; return empty(); }
  if (endpoint === 'Me/Scrobbling') return empty();
  if (endpoint === 'Me/Delivery') return json(state.delivery);
  if (endpoint === 'Me/Delivery/Retry') return empty();
  if (endpoint.startsWith('Me/History')) return history(endpoint, method, url, state);
  if (endpoint === 'Me/Overview') return json({ Tracks: [track], Artists: [{ Name: track.Artist, PlayCount: 80 }], Albums: [{ Name: track.Album, PlayCount: 50 }], Statistics: { Scrobbles: 1000, Artists: 20, Albums: 50, Tracks: 100 } });
  if (endpoint === 'Me/Library/Search') return json([{ ItemId: item, Name: track.Title, Artist: track.Artist, Kind: 'track' }]);
  if (endpoint === 'Me/Discovery') return json({ Local: [{ Track: track, ItemId: item, Status: 'Matched' }], External: [{ ...track, Title: '<img src=x onerror=alert(1)>', Url: 'javascript:alert(1)' }], Artists: [{ Name: 'Portishead', Kind: 'artist', Url: 'https://www.last.fm/music/Portishead' }], Albums: [{ Name: 'Dummy', Artist: 'Portishead', Kind: 'album', Url: 'https://www.last.fm/music/Portishead/Dummy' }] });
  if (endpoint.startsWith('Me/Favourites')) return favourites(endpoint, method, body, state);
  if (endpoint.startsWith('Me/Playlists')) return playlists(endpoint, method, body, state);
  return { status: 404, body: 'No fixture response' };
}

function history(endpoint, method, url, state) {
  if (endpoint === 'Me/History') return json({ Tracks: state.historyTracks, Page: Number(url.searchParams.get('page') || 1), TotalPages: 2, Complete: true, Until: 1788800000 });
  if (endpoint === 'Me/History/Import') {
    state.appliedCount = Math.min(201, (state.appliedCount ?? 0) + 200);
    state.imported = state.appliedCount === 201;
    state.preview = !state.imported;
    return json({ Applied: state.appliedCount, Total: 201, Complete: state.imported });
  }
  if (endpoint === 'Me/History/Continue') state.previewComplete = true;
  if (method === 'POST') state.preview = true;
  if (!state.preview) return empty();
  const page = Number(url.searchParams.get('page') || 1);
  return json({ Id: previewId, ExpiresAt: '2026-09-08T15:00:00Z', Entries: [{ Track: { ...track, Title: page === 1 ? track.Title : 'Angel' }, CurrentPlayCount: 10, ProposedPlayCount: 42, ProposedLastPlayed: '2026-09-01T12:00:00Z' }], Unmatched: [{ Track: { ...track, Title: 'Ambiguous song' }, Status: 'Ambiguous' }], MatchedCount: 201, UnmatchedCount: 1, Complete: Boolean(state.previewComplete), CountsComplete: true, DatesComplete: Boolean(state.previewComplete), NextPage: 2, RecentNextPage: 1, AppliedCount: state.appliedCount ?? 0, Page: page, Pages: 2 });
}

function favourites(endpoint, method, body, state) {
  if (method === 'PUT') state.favourites = body.enabled;
  if (endpoint === 'Me/Favourites/Review') state.removed = true;
  return json({ Enabled: state.favourites, Status: 'Ready', Total: state.removed ? 0 : 1, Page: 1, Pages: 1, Pending: state.removed ? [] : [{ Id: previewId, Track: track, RemoveFrom: 'lastfm' }] });
}

function playlists(endpoint, method, body, state) {
  if (endpoint === 'Me/Playlists/Pending') return json(state.pendingPlaylist ?? null);
  if (endpoint.startsWith('Me/Playlists/Pending/') && method === 'DELETE') { state.pendingPlaylist = null; return empty(); }
  if (method === 'POST') state.recipes = [{ ...body, id: previewId, playlistId: item }];
  if (method === 'DELETE') { state.recipes = []; return empty(); }
  return json(state.recipes);
}

export async function passwordLogin(page) {
  await page.getByText('Sign in with a password', { exact: true }).click();
  await page.getByLabel('Jellyfin username').fill('listener');
  await page.getByLabel('Jellyfin password').fill('fixture-password');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await page.locator('#signed-in').waitFor({ state: 'visible' });
}
