import { Client } from './client.js';
import { bind, element, line, link, message } from './view.js';
import { t } from './i18n.js';

export interface Track { artist: string; title: string; album?: string; url?: string; playCount: number; playedAt?: string; nowPlaying: boolean }
interface Page { tracks: Track[]; page: number; totalPages: number; complete: boolean; until?: number }
interface Match { track: Track; itemId?: string; status: string }
interface Preview { id: string; entries: { track: Track; currentPlayCount: number; proposedPlayCount: number; currentLastPlayed?: string; proposedLastPlayed?: string }[]; unmatched: Match[]; matchedCount: number; unmatchedCount: number; complete: boolean; page: number; pages: number; countsComplete: boolean; datesComplete: boolean; nextPage: number; recentNextPage: number; appliedCount: number }
interface Overview { tracks: Track[]; artists: { name: string; playCount: number; url?: string }[]; albums: { name: string; artist?: string; playCount: number; url?: string }[]; statistics: { scrobbles: number; artists: number; albums: number; tracks: number } }
interface DiscoveryEntity { kind: string; name: string; artist?: string; url?: string; itemId?: string }

export function renderTracks(parent: HTMLElement, tracks: Track[], client?: Client, matches?: Match[]): void {
  parent.replaceChildren();
  if (!tracks.length) { line(parent, t('music_empty'), 'muted'); return; }
  for (const [index, track] of tracks.slice(0, 200).entries()) {
    const row = document.createElement('li');
    const title = document.createElement('strong');
    title.textContent = track.title;
    row.append(title);
    line(row, `${track.artist}${track.album ? ` · ${track.album}` : ''}`, 'muted');
    if (track.nowPlaying) line(row, t('music_playing_now'));
    else if (track.playedAt) line(row, new Date(track.playedAt).toLocaleString(), 'detail');
    else if (track.playCount) line(row, t('music_plays_count', { count: track.playCount.toLocaleString() }), 'detail');
    const itemId = matches?.[index]?.itemId;
    if (client && itemId) link(row, t('music_open_in_jellyfin'), `${client.root}/web/index.html#/details?id=${encodeURIComponent(itemId)}&serverId=${encodeURIComponent(client.config.serverId)}`);
    if (track.url) link(row, t('music_lastfm_link'), track.url, true);
    parent.append(row);
  }
}

export function setupMusic(client: Client): void {
  let page = 1;
  let until: number | undefined;
  let historyRequest: AbortController | undefined;
  const resetHistory = (): void => { historyRequest?.abort(); historyRequest = undefined; page = 1; until = undefined; };
  client.addEventListener('sessioncleared', resetHistory);
  client.addEventListener('accountchanged', resetHistory);
  const loadHistory = async (): Promise<void> => {
    historyRequest?.abort();
    const current = new AbortController();
    historyRequest = current;
    const requestedPage = page;
    const data = await client.api<Page>(`Me/History?page=${requestedPage}${until === undefined ? '' : `&until=${until}`}`, 'GET', undefined, current.signal);
    current.signal.throwIfAborted();
    page = data.page;
    until = data.until;
    renderTracks(element('history-list'), data.tracks);
    element('history-page').textContent = t('history_page', { page: data.page, totalPages: data.totalPages });
    element<HTMLButtonElement>('history-previous').disabled = page === 1;
    element<HTMLButtonElement>('history-next').disabled = page >= data.totalPages;
    message(t('history_loaded'));
  };
  bind('history-refresh', async () => { page = 1; until = undefined; await loadHistory(); });
  bind('history-previous', async () => { page = Math.max(1, page - 1); await loadHistory(); });
  bind('history-next', async () => { page++; await loadHistory(); });
  bind('charts-refresh', async () => {
    const period = element<HTMLSelectElement>('chart-period').value;
    const overview = await client.api<Overview>(`Me/Overview?period=${encodeURIComponent(period)}`);
    renderTracks(element('chart-tracks'), overview.tracks);
    const statistics = overview.statistics;
    element('listening-stats').textContent = t('music_stats', {
      scrobbles: statistics.scrobbles.toLocaleString(),
      artists: statistics.artists.toLocaleString(),
      albums: statistics.albums.toLocaleString()
    });
    for (const [id, entries] of [['chart-artists', overview.artists], ['chart-albums', overview.albums]] as const) {
      const list = element(id); list.replaceChildren();
      for (const item of entries.slice(0, 50)) {
        const row = document.createElement('div');
        line(row, `${item.name} · ${t('music_plays_count', { count: item.playCount.toLocaleString() })}`);
        if (item.url) link(row, t('music_lastfm_link'), item.url, true);
        list.append(row);
      }
    }
    message(t('music_charts_loaded'));
  });
  setupHistoryImport(client);
  bind('discovery-refresh', async () => {
    const seed = element<HTMLSelectElement>('discovery-seed').value.trim();
    const result = await client.api<{ local: Match[]; external: Track[]; artists: DiscoveryEntity[]; albums: DiscoveryEntity[] }>(`Me/Discovery${seed ? `?seedItemId=${encodeURIComponent(seed)}` : ''}`);
    renderTracks(element('discovery-local'), result.local.map(match => match.track), client, result.local);
    renderTracks(element('discovery-external'), result.external);
    renderEntities(element('discovery-artists'), result.artists, client);
    renderEntities(element('discovery-albums'), result.albums, client);
    message(t('music_discovery_refreshed'));
  });
}

function setupHistoryImport(client: Client): void {
  let preview: Preview | undefined;
  client.addEventListener('sessioncleared', () => { preview = undefined; });
  client.addEventListener('accountchanged', () => { preview = undefined; });
  const render = (value: Preview | undefined): void => { preview = value; showPreview(value); };
  const load = async (page = 1): Promise<void> => { render(await client.api<Preview | undefined>(`Me/History/Preview?page=${page}`)); };
  bind('history-preview', async () => { render(await client.api<Preview>('Me/History/Preview', 'POST')); });
  bind('history-resume', async () => { await load(); });
  bind('preview-previous', async () => { if (preview) await load(preview.page - 1); });
  bind('preview-next', async () => { if (preview) await load(preview.page + 1); });
  bind('history-continue', async () => {
    if (!preview) return;
    render(await client.api<Preview>('Me/History/Continue', 'POST', { previewId: preview.id }));
  });
  bind('history-apply', async () => {
    if (!preview) throw new Error(t('history_import_need_preview'));
    const result = await client.api<{ applied: number; total: number; complete: boolean }>('Me/History/Import', 'POST', { previewId: preview.id });
    if (result.complete) render(undefined);
    else await load();
    message(result.complete
      ? t('history_import_preview_complete', { applied: result.applied })
      : t('history_import_partial', { applied: result.applied, total: result.total }));
  });
}

function showPreview(preview: Preview | undefined): void {
  const output = element('history-preview-list'); output.replaceChildren();
  const apply = element<HTMLButtonElement>('history-apply');
  apply.disabled = !preview?.complete || preview.matchedCount <= preview.appliedCount;
  apply.textContent = preview?.appliedCount ? t('history_import_continue_confirmed') : t('history_import_confirm');
  element('history-continue').hidden = !preview || preview.complete;
  element('preview-pagination').hidden = !preview;
  if (!preview) { message(t('history_import_no_preview')); return; }
  line(output, t('history_import_matches', { matched: preview.matchedCount, unmatched: preview.unmatchedCount }));
  const counts = preview.countsComplete ? t('history_import_ready') : t('history_import_counts_page', { page: Math.max(0, preview.nextPage - 1) });
  const dates = preview.datesComplete ? t('history_import_ready') : t('history_import_dates_page', { page: Math.max(0, preview.recentNextPage - 1) });
  line(output, t('history_import_progress_status', { counts, dates }));
  if (preview.appliedCount) line(output, t('history_import_applied_partial', { applied: preview.appliedCount, matched: preview.matchedCount }));
  line(output, t('history_import_higher_counts_notice'));
  for (const entry of preview.entries) {
    line(output, t('history_import_track_change', { artist: entry.track.artist, title: entry.track.title, current: entry.currentPlayCount, proposed: entry.proposedPlayCount }));
    if (entry.proposedLastPlayed) {
      const current = entry.currentLastPlayed ? new Date(entry.currentLastPlayed).toLocaleString() : t('history_import_unknown_date');
      const proposed = new Date(entry.proposedLastPlayed).toLocaleString();
      line(output, t('history_import_last_played', { current, proposed }), 'detail');
    }
  }
  for (const match of preview.unmatched) line(output, t('history_import_skipped', { artist: match.track.artist, title: match.track.title, status: match.status }), 'muted');
  if (!preview.complete) line(output, t('history_import_continue_notice'));
  element('preview-page').textContent = t('history_import_preview_page', { page: preview.page, pages: preview.pages });
  element<HTMLButtonElement>('preview-previous').disabled = preview.page === 1;
  element<HTMLButtonElement>('preview-next').disabled = preview.page >= preview.pages;
  message(t('history_import_review_notice'));
}

function renderEntities(parent: HTMLElement, entries: DiscoveryEntity[], client: Client): void {
  parent.replaceChildren();
  for (const entry of entries.slice(0, 100)) {
    const row = document.createElement('li');
    line(row, `${entry.name}${entry.artist ? ` · ${entry.artist}` : ''}`);
    if (entry.itemId) link(row, t('music_open_in_jellyfin'), `${client.root}/web/index.html#/details?id=${encodeURIComponent(entry.itemId)}&serverId=${encodeURIComponent(client.config.serverId)}`);
    if (entry.url) link(row, t('music_lastfm_link'), entry.url, true);
    parent.append(row);
  }
  if (!entries.length) line(parent, t('music_discovery_empty'), 'muted');
}
