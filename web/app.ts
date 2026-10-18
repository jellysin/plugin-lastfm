import { Client, readJson, type Bootstrap } from './client.js';
import { setupAuthentication } from './auth.js';
import { refreshAccount, setupAccount, takeRefusedReturn } from './account.js';
import { setupMusic } from './music.js';
import { setupFavourites } from './favourites.js';
import { setupPlaylists } from './playlists.js';
import { setupScrobbleLog } from './rules.js';
import { bind, element, message } from './view.js';
import { applyTranslations, t } from './i18n.js';

async function start(): Promise<void> {
  applyTranslations();
  const response = await fetch('./Bootstrap', { credentials: 'omit', cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(90_000) });
  if (!response.ok) throw new Error(t('app_failed_config_load'));
  const config = await readJson(response, 4096) as Bootstrap;
  let storage: Storage | undefined;
  try { storage = sessionStorage; } catch { storage = undefined; /* Private browser settings may disable storage; the session then lives in memory only. */ }
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
  setupScrobbleLog(client);
  for (const prefix of ['discovery', 'playlist']) bind(`${prefix}-search-button`, async () => {
    const query = element<HTMLInputElement>(`${prefix}-search`).value.trim();
    if (query.length < 2) throw new Error(t('app_search_placeholder_min'));
    const matches = await client.api<{ itemId: string; name: string; artist?: string; kind: string }[]>(`Me/Library/Search?query=${encodeURIComponent(query)}`);
    const select = element<HTMLSelectElement>(`${prefix}-seed`);
    while (select.options.length > 1) select.remove(1);
    for (const match of matches.slice(0, 20)) {
      const option = document.createElement('option'); option.value = match.itemId;
      option.textContent = `${match.name}${match.artist ? ` · ${match.artist}` : ''} (${match.kind})`;
      select.append(option);
    }
    if (matches.length) select.selectedIndex = 1;
    message(matches.length ? t('app_search_choose_seed') : t('app_search_no_matches'));
  });
  const jellyfin = element<HTMLAnchorElement>('jellyfin-link');
  jellyfin.href = `${client.root}/web/index.html`;
  bind('delivery-refresh', async () => {
    const state = await client.api<Delivery>('Me/Delivery');
    element('delivery-status').textContent = deliveryStatus(state);
  });
  bind('delivery-retry', async () => { await client.api('Me/Delivery/Retry', 'POST'); message(t('app_delivery_queued')); });
  const refused = takeRefusedReturn();
  await refresh();
  if (refused) message(t('app_connection_failed_retry'), true);
}

interface Delivery {
  pending: number; blocked: number; lastErrorCode?: number; lastIgnoredCode?: number;
  rejected: number; lastRejectedCode?: number; oldestPendingDays?: number; pendingPersistence: number;
  legacyPluginDetected: boolean; droppedSnapshots: number; failedWrites: number;
}

// The plugin's own threshold: Last.fm documents no fixed age limit, only that it ignores plays it finds too old.
const HELD_PLAY_WARNING_DAYS = 7;

function deliveryStatus(state: Delivery): string {
  const details = [
    t('app_delivery_saved', { pending: state.pending }),
    t('app_delivery_awaiting_retry', { blocked: state.blocked })
  ];
  if (state.lastIgnoredCode === 5) details.push(t('app_delivery_daily_limit'));
  else if (state.lastIgnoredCode === 4) details.push(t('app_delivery_clock_skew'));
  else if (state.lastErrorCode === 29 || state.lastErrorCode === 429) details.push(t('app_delivery_rate_limit'));
  else if (state.lastErrorCode === 9) details.push(t('app_delivery_reconnect'));
  else if (state.lastErrorCode) details.push(t('app_delivery_error', { code: state.lastErrorCode }));
  if ((state.oldestPendingDays ?? 0) >= HELD_PLAY_WARNING_DAYS) details.push(t('app_delivery_held_play', { days: state.oldestPendingDays ?? 0 }));
  if (state.rejected) {
    details.push(state.lastRejectedCode
      ? t('app_delivery_rejected_reason', { rejected: state.rejected, reason: state.lastRejectedCode })
      : t('app_delivery_rejected', { rejected: state.rejected }));
  }
  if (state.pendingPersistence) details.push(t('app_delivery_pending_persistence', { pending: state.pendingPersistence }));
  if (state.legacyPluginDetected) details.push(t('app_delivery_legacy_plugin'));
  if (state.droppedSnapshots || state.failedWrites) details.push(t('app_delivery_unwritten_events'));
  return details.join(' · ');
}

void start().catch((error: unknown) => { message(error instanceof Error ? error.message : t('app_page_start_failed'), true); });

function clearPrivateView(): void {
  message('');
  for (const id of ['history-list', 'history-preview-list', 'history-page', 'listening-stats', 'chart-tracks', 'chart-artists', 'chart-albums', 'removal-list', 'favourites-status', 'favourites-page', 'discovery-local', 'discovery-external', 'discovery-artists', 'discovery-albums', 'playlist-list', 'playlist-pending', 'profile-link', 'connection-status', 'delivery-status']) element(id).replaceChildren();
  for (const id of ['disconnect-confirmation', 'preview-pagination', 'history-continue', 'administrator']) element(id).hidden = true;
  for (const el of document.querySelectorAll<HTMLElement>('[data-needs-connection]')) el.hidden = true;
  for (const id of ['history-apply', 'history-previous', 'history-next', 'favourites-previous', 'favourites-next']) element<HTMLButtonElement>(id).disabled = true;
  for (const form of element('signed-in').querySelectorAll('form')) form.reset();
  for (const id of ['discovery-seed', 'playlist-seed']) {
    const select = element<HTMLSelectElement>(id);
    while (select.options.length > 1) select.remove(1);
  }
}
