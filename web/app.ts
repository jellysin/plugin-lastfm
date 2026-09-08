import { Client, readJson, type Bootstrap } from './client.js';
import { setupAuthentication } from './auth.js';
import { refreshAccount, setupAccount } from './account.js';
import { setupMusic } from './music.js';
import { setupFavourites } from './favourites.js';
import { setupPlaylists } from './playlists.js';
import { bind, element, message } from './view.js';

async function start(): Promise<void> {
  const response = await fetch('./Bootstrap', { credentials: 'omit', cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(90_000) });
  if (!response.ok) throw new Error('The plugin could not load its server configuration.');
  const config = await readJson(response, 4096) as Bootstrap;
  let storage: Storage | undefined;
  try { storage = sessionStorage; } catch { /* Private browser settings may disable storage. */ }
  const client = new Client(config, storage);
  client.addEventListener('sessioncleared', () => {
    clearPrivateView();
    void refreshAccount(client);
  });
  client.addEventListener('accountchanged', clearPrivateView);
  const refresh = async (): Promise<void> => {
    try { await refreshAccount(client); }
    catch (error) { if (!client.signedIn) await refreshAccount(client); throw error; }
  };
  setupAuthentication(client, refresh);
  setupAccount(client, refresh);
  setupMusic(client);
  setupFavourites(client);
  setupPlaylists(client);
  for (const prefix of ['discovery', 'playlist']) bind(`${prefix}-search-button`, async () => {
    const query = element<HTMLInputElement>(`${prefix}-search`).value.trim();
    if (query.length < 2) throw new Error('Enter at least two characters to search.');
    const matches = await client.api<{ itemId: string; name: string; artist?: string; kind: string }[]>(`Me/Library/Search?query=${encodeURIComponent(query)}`);
    const select = element<HTMLSelectElement>(`${prefix}-seed`);
    while (select.options.length > 1) select.remove(1);
    for (const match of matches.slice(0, 20)) {
      const option = document.createElement('option'); option.value = match.itemId;
      option.textContent = `${match.name}${match.artist ? ` · ${match.artist}` : ''} (${match.kind})`;
      select.append(option);
    }
    if (matches.length) select.selectedIndex = 1;
    message(matches.length ? 'Choose a starting point from your library.' : 'No matching tracks or artists in your library.');
  });
  const jellyfin = element<HTMLAnchorElement>('jellyfin-link');
  jellyfin.href = `${client.root}/web/index.html`;
  bind('delivery-refresh', async () => {
    const state = await client.api<{ pending: number; blocked: number; legacyPluginDetected: boolean; droppedSnapshots: number; failedWrites: number }>('Me/Delivery');
    element('delivery-status').textContent = `${state.pending} pending · ${state.blocked} awaiting retry${state.legacyPluginDetected ? ' · remove the legacy plugin to enable delivery' : ''}${state.droppedSnapshots || state.failedWrites ? ' · some playback events could not be saved; check server logs' : ''}`;
  });
  bind('delivery-retry', async () => { await client.api('Me/Delivery/Retry', 'POST'); message('Retained submissions queued for retry.'); });
  await refresh();
}

void start().catch((error: unknown) => { message(error instanceof Error ? error.message : 'The page could not start.', true); });

function clearPrivateView(): void {
  for (const id of ['history-list', 'history-preview-list', 'history-page', 'listening-stats', 'chart-tracks', 'chart-artists', 'chart-albums', 'removal-list', 'favourites-status', 'favourites-page', 'discovery-local', 'discovery-external', 'discovery-artists', 'discovery-albums', 'playlist-list', 'profile-link', 'connection-status', 'application-status', 'delivery-status']) element(id).replaceChildren();
  for (const id of ['finish-connection', 'disconnect-confirmation', 'preview-pagination', 'history-continue', 'administrator']) element(id).hidden = true;
  for (const id of ['history-apply', 'history-previous', 'history-next', 'favourites-previous', 'favourites-next']) element<HTMLButtonElement>(id).disabled = true;
  element<HTMLAnchorElement>('authorize-lastfm').removeAttribute('href');
  for (const form of element('signed-in').querySelectorAll('form')) form.reset();
  for (const id of ['discovery-seed', 'playlist-seed']) {
    const select = element<HTMLSelectElement>(id);
    while (select.options.length > 1) select.remove(1);
  }
}
