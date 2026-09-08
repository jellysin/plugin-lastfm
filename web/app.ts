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
    const state = await client.api<Delivery>('Me/Delivery');
    element('delivery-status').textContent = deliveryStatus(state);
  });
  bind('delivery-retry', async () => { await client.api('Me/Delivery/Retry', 'POST'); message('Retained submissions queued for retry.'); });
  await refresh();
}

interface Delivery {
  pending: number; blocked: number; lastErrorCode?: number; lastIgnoredCode?: number;
  rejected: number; lastRejectedCode?: number; pendingPersistence: number;
  legacyPluginDetected: boolean; droppedSnapshots: number; failedWrites: number;
}

function deliveryStatus(state: Delivery): string {
  const details = [`${state.pending} saved for delivery`, `${state.blocked} awaiting retry`];
  if (state.lastIgnoredCode === 5) details.push('Last.fm daily limit reached; submissions retained');
  else if (state.lastErrorCode === 29 || state.lastErrorCode === 429) details.push('Last.fm rate limit reached; submissions retained');
  else if (state.lastErrorCode === 9) details.push('Reconnect Last.fm to resume delivery');
  else if (state.lastErrorCode) details.push(`Last.fm error ${state.lastErrorCode}`);
  if (state.rejected) details.push(`${state.rejected} rejected by Last.fm${state.lastRejectedCode ? ` (reason ${state.lastRejectedCode})` : ''}`);
  if (state.pendingPersistence) details.push(`${state.pendingPersistence} still waiting for storage; these listens cannot yet survive a server restart`);
  if (state.legacyPluginDetected) details.push('Remove the legacy plugin to enable delivery');
  if (state.droppedSnapshots || state.failedWrites) details.push('Some playback events could not be saved; check server logs');
  return details.join(' · ');
}

void start().catch((error: unknown) => { message(error instanceof Error ? error.message : 'The page could not start.', true); });

function clearPrivateView(): void {
  message('');
  for (const id of ['history-list', 'history-preview-list', 'history-page', 'listening-stats', 'chart-tracks', 'chart-artists', 'chart-albums', 'removal-list', 'favourites-status', 'favourites-page', 'discovery-local', 'discovery-external', 'discovery-artists', 'discovery-albums', 'playlist-list', 'playlist-pending', 'profile-link', 'connection-status', 'delivery-status']) element(id).replaceChildren();
  for (const id of ['finish-connection', 'disconnect-confirmation', 'preview-pagination', 'history-continue', 'administrator']) element(id).hidden = true;
  for (const id of ['history-apply', 'history-previous', 'history-next', 'favourites-previous', 'favourites-next']) element<HTMLButtonElement>(id).disabled = true;
  element<HTMLAnchorElement>('authorize-lastfm').removeAttribute('href');
  for (const form of element('signed-in').querySelectorAll('form')) form.reset();
  for (const id of ['discovery-seed', 'playlist-seed']) {
    const select = element<HTMLSelectElement>(id);
    while (select.options.length > 1) select.remove(1);
  }
}
