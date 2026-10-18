import { readFile } from 'node:fs/promises';

export const token = 'a'.repeat(32);
export const track = { Artist: 'Massive Attack', Title: 'Teardrop', Album: 'Mezzanine', PlayCount: 42, Url: 'https://www.last.fm/music/Massive+Attack/_/Teardrop', NowPlaying: false };
export const lastfmToken = 'fixture-lastfm-token';
const binding = 'fixture-binding';
const item = '11111111-1111-4111-8111-111111111111';
const previewId = '22222222-2222-4222-8222-222222222222';
const json = body => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
const empty = () => ({ status: 204 });
// The test runner does not route the request that follows a redirect it answered itself, so each hop is a navigation of its own.
const forward = address => ({ status: 200, contentType: 'text/html', body: `<!doctype html><meta http-equiv="refresh" content="0;url=${address.replaceAll('&', '&amp;').replaceAll('"', '%22')}">` });
const webRoot = new URL('../../src/JellySin.Plugin.Lastfm/Web/', import.meta.url);

export async function installServer(page, { prefix = '', administrator = false, latencyMs = 0 } = {}) {
  const state = { requests: [], lastfm: [], approvalPending: false, lastfmAnswer: 'token', prefix, administrator, connected: true, favourites: false, favouritesRunning: false, removed: false, recipes: [], imported: false, preview: false, disconnected: false,
    historyTracks: [track], delivery: { Pending: 3, Blocked: 1, LegacyPluginDetected: false, DroppedSnapshots: 0, FailedWrites: 0, Rejected: 0, PendingPersistence: 0 },
    devices: [
      { Id: 'dev-1', Name: 'Living Room TV', CustomName: null, AppName: 'Android TV', DateLastActivity: '2026-10-15T12:00:00Z' },
      { Id: 'dev-2', Name: 'Phone', CustomName: 'My Phone', AppName: 'Mobile', DateLastActivity: '2026-10-16T12:00:00Z' },
    ],
    excludedDevices: [], deviceFail: false,
    adminMediaTypes: { ScrobbleMusic: true, ScrobbleAudiobooks: false, ScrobbleMusicVideos: true },
    excludedMediaTypes: [], mediaTypesFail: false,
    scrobbleLogEnabled: false, scrobbleLogCount: 0 };
  await page.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.origin === 'https://www.last.fm' && url.pathname === '/api/auth/') return route.fulfill(lastfm(url, state));
    if (url.origin !== 'http://127.0.0.1:4177' || !url.pathname.startsWith(prefix + '/')) return route.abort();
    const path = url.pathname.slice(prefix.length);
    if (latencyMs) await new Promise(resolve => setTimeout(resolve, latencyMs));
    state.requests.push({ path, url: url.href, method: request.method(), headers: request.headers(), body: request.postDataJSON() });
    if (path === '/JellySin/Lastfm/Callback') return route.fulfill(await callback(page, url, state));
    if (path === '/JellySin/Lastfm/') return route.fulfill({ contentType: 'text/html', body: await readFile(new URL('index.html', webRoot), 'utf8') });
    if (path.startsWith('/JellySin/Lastfm/Assets/')) {
      const name = path.split('/').at(-1);
      if (!/^[a-z0-9-]+\.(js|css)$/.test(name)) return route.abort();
      return route.fulfill({ contentType: name.endsWith('.js') ? 'text/javascript' : 'text/css', body: await readFile(new URL(name, webRoot), 'utf8') });
    }
    const response = respond(path, request.method(), request.postDataJSON(), url, state);
    return route.fulfill(response);
  });
  await page.goto(`http://127.0.0.1:4177${prefix}/JellySin/Lastfm/`);
  return state;
}

/** Last.fm sends the browser back to the address it was given, with a token appended. */
function lastfm(url, state) {
  state.lastfm.push(url.href);
  const back = url.searchParams.get('cb');
  return forward(state.lastfmAnswer === 'token' ? `${back}&token=${lastfmToken}` : back);
}

/** Like the server: the return counts only in the browser that holds the cookie of the attempt, and the token is not passed on. */
async function callback(page, url, state) {
  const cookies = await page.context().cookies(url.href);
  const bound = cookies.some(cookie => cookie.name === 'JellySinLastfmAttempt' && cookie.value === binding && cookie.httpOnly);
  state.approvalPending = bound && url.searchParams.get('token') === lastfmToken && url.searchParams.get('attempt') === previewId.replaceAll('-', '');
  return forward(`${state.prefix}/JellySin/Lastfm/${state.approvalPending ? '' : '?connection=failed'}`);
}

function begin(url, state) {
  if (state.applicationMissing) return applicationMissing;
  const back = `${url.origin}${state.prefix}/JellySin/Lastfm/Callback?attempt=${previewId.replaceAll('-', '')}`;
  return { ...json({ AuthorizationUrl: state.authorizationUrl ?? `https://www.last.fm/api/auth/?api_key=fixture&cb=${encodeURIComponent(back)}`, ExpiresAt: '2026-09-08T15:10:00Z' }),
    headers: { 'Set-Cookie': `JellySinLastfmAttempt=${binding}; Path=${state.prefix}/JellySin/Lastfm/Callback; Max-Age=600; HttpOnly; SameSite=Lax` } };
}

function finish(state) {
  if (!state.approvalPending) return { status: 409, body: 'No approved attempt' };
  state.approvalPending = false;
  if (state.finishFails) return { status: 502, body: 'Last.fm refused' };
  state.connected = true;
  return json(connection(state));
}

// ApiExceptionFilter's answer when the server has no usable Last.fm application; the page must show its own text, not this title.
const applicationMissing = { status: 503, contentType: 'application/problem+json',
  body: JSON.stringify({ type: 'urn:jellysin:lastfm:application-missing', title: 'Server text the page must not show.', status: 503 }) };

const connection = state => ({ Connected: state.connected, Username: state.connected ? 'Listener' : null, NeedsReconnect: Boolean(state.needsReconnect), ScrobblingEnabled: true, ApprovalPending: state.approvalPending });

function respond(path, method, body, url, state) {
  if (path === '/JellySin/Lastfm/Bootstrap') return json({ BasePath: state.prefix, ServerId: 'fixture-server', Version: '1.0.0' });
  if (path === '/Users/AuthenticateByName' || path === '/Users/AuthenticateWithQuickConnect') return json({ AccessToken: token, ServerId: 'fixture-server', User: { Name: 'listener' } });
  if (path === '/QuickConnect/Enabled') return json(true);
  if (path === '/QuickConnect/Initiate') return json({ Secret: 'q'.repeat(32), Code: '123456' });
  if (path === '/JellySin/Lastfm/Auth/QuickConnect/Status') return json({ Authenticated: true });
  if (path === '/Sessions/Logout') return empty();
  const endpoint = path.replace('/JellySin/Lastfm/', '');
  if (endpoint === 'Me') return json({ UserName: 'listener', IsAdministrator: state.administrator, Connection: connection(state) });
  if (endpoint === 'Admin/Application') return method === 'GET' ? json({ Configured: true, Source: 'Project' }) : empty();
  if (endpoint === 'Me/Connection/Begin') return begin(url, state);
  if (endpoint === 'Me/Connection/Finish') return finish(state);
  if (endpoint === 'Me/Connection/Refusal') return state.refusalFails ? { status: 503, body: '' } : empty();
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
  if (endpoint === 'Me/Devices') return devicesResponse(method, body, state);
  if (endpoint === 'Me/MediaTypes') return mediaTypesResponse(method, body, state);
  if (endpoint.startsWith('Me/ScrobbleLog')) return scrobbleLogResponse(endpoint, method, body, url, state);
  return { status: 404, body: 'No fixture response' };
}

function scrobbleLogResponse(endpoint, method, body, url, state) {
  if (endpoint === 'Me/ScrobbleLog/Settings') {
    if (method === 'PUT' || method === 'POST') {
      state.scrobbleLogEnabled = Boolean(body?.enabled);
      if (!state.scrobbleLogEnabled) state.scrobbleLogCount = 0;
    }
    return json({ Enabled: state.scrobbleLogEnabled, Count: state.scrobbleLogCount });
  }
  if (endpoint.startsWith('Me/ScrobbleLog/Export')) {
    const format = url.searchParams.get('format');
    if (format === 'csv') return { status: 200, contentType: 'text/csv', body: 'OccurrenceId,CompletedAt,StartedAt,Artist,Title,Album,AlbumArtist,MusicBrainzId,ResultCode\r\n' };
    if (format === 'json') return json([]);
    return { status: 400, body: 'Invalid format' };
  }
  return { status: 404, body: 'No fixture response' };
}

function devicesResponse(method, body, state) {
  if (state.deviceFail) return { status: 503, body: 'Could not load devices' };
  if (method === 'PUT') state.excludedDevices = body?.excludedDevices ?? [];
  return json(state.devices.map(d => ({
    Id: d.Id,
    Name: d.CustomName ?? d.Name,
    AppName: d.AppName,
    IsExcluded: state.excludedDevices.includes(d.Id),
  })));
}

function mediaTypesResponse(method, body, state) {
  if (state.mediaTypesFail) return { status: 503, body: 'Could not load media types' };
  if (method === 'PUT') state.excludedMediaTypes = body?.excludedMediaTypes ?? [];
  return json([
    { Type: 'Music', IsAdminAllowed: Boolean(state.adminMediaTypes.ScrobbleMusic), IsUserExcluded: state.excludedMediaTypes.includes('Music'), IsScrobbled: Boolean(state.adminMediaTypes.ScrobbleMusic) && !state.excludedMediaTypes.includes('Music') },
    { Type: 'Audiobooks', IsAdminAllowed: Boolean(state.adminMediaTypes.ScrobbleAudiobooks), IsUserExcluded: state.excludedMediaTypes.includes('Audiobooks'), IsScrobbled: Boolean(state.adminMediaTypes.ScrobbleAudiobooks) && !state.excludedMediaTypes.includes('Audiobooks') },
    { Type: 'MusicVideos', IsAdminAllowed: Boolean(state.adminMediaTypes.ScrobbleMusicVideos), IsUserExcluded: state.excludedMediaTypes.includes('MusicVideos'), IsScrobbled: Boolean(state.adminMediaTypes.ScrobbleMusicVideos) && !state.excludedMediaTypes.includes('MusicVideos') },
  ]);
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
  return json({ Enabled: state.favourites, Status: 'Ready', Total: state.removed ? 0 : 1, Page: 1, Pages: 1, Running: Boolean(state.favouritesRunning), Pending: state.removed ? [] : [{ Id: previewId, Track: track, RemoveFrom: 'lastfm' }] });
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
