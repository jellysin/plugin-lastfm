import { Client } from './client.js';
import { bind, element, line, link, message } from './view.js';

export interface Track { artist: string; title: string; album?: string; url?: string; playCount: number; playedAt?: string; nowPlaying: boolean }
interface Page { tracks: Track[]; page: number; totalPages: number; complete: boolean; until?: number }
interface Match { track: Track; itemId?: string; status: string }
interface Preview { id: string; entries: { track: Track; currentPlayCount: number; proposedPlayCount: number; currentLastPlayed?: string; proposedLastPlayed?: string }[]; unmatched: Match[]; matchedCount: number; unmatchedCount: number; complete: boolean; page: number; pages: number }
interface Overview { tracks: Track[]; artists: { name: string; playCount: number; url?: string }[]; albums: { name: string; artist?: string; playCount: number; url?: string }[]; statistics: { scrobbles: number; artists: number; albums: number; tracks: number } }
interface DiscoveryEntity { kind: string; name: string; artist?: string; url?: string; itemId?: string }

export function renderTracks(parent: HTMLElement, tracks: Track[], client?: Client, matches?: Match[]): void {
  parent.replaceChildren();
  if (!tracks.length) { line(parent, 'No tracks to display yet.', 'muted'); return; }
  for (const [index, track] of tracks.slice(0, 200).entries()) {
    const row = document.createElement('li');
    const title = document.createElement('strong');
    title.textContent = track.title;
    row.append(title);
    line(row, `${track.artist}${track.album ? ` · ${track.album}` : ''}`, 'muted');
    if (track.nowPlaying) line(row, 'Playing now');
    else if (track.playedAt) line(row, new Date(track.playedAt).toLocaleString(), 'detail');
    else if (track.playCount) line(row, `${track.playCount.toLocaleString()} plays`, 'detail');
    const itemId = matches?.[index]?.itemId;
    if (client && itemId) link(row, 'Open in Jellyfin', `${client.root}/web/index.html#!/details?id=${encodeURIComponent(itemId)}&serverId=${encodeURIComponent(client.config.serverId)}`);
    if (track.url) link(row, 'Last.fm ↗', track.url, true);
    parent.append(row);
  }
}

export function setupMusic(client: Client): void {
  let page = 1;
  let until: number | undefined;
  const loadHistory = async (): Promise<void> => {
    const data = await client.api<Page>(`Me/History?page=${page}${until === undefined ? '' : `&until=${until}`}`);
    until = data.until;
    renderTracks(element('history-list'), data.tracks);
    element('history-page').textContent = `Page ${data.page} of ${data.totalPages}`;
    element<HTMLButtonElement>('history-previous').disabled = page === 1;
    element<HTMLButtonElement>('history-next').disabled = page >= data.totalPages;
    message('Listening history loaded.');
  };
  bind('history-refresh', async () => { page = 1; until = undefined; await loadHistory(); });
  bind('history-previous', async () => { page = Math.max(1, page - 1); await loadHistory(); });
  bind('history-next', async () => { page++; await loadHistory(); });
  bind('charts-refresh', async () => {
    const period = element<HTMLSelectElement>('chart-period').value;
    const overview = await client.api<Overview>(`Me/Overview?period=${encodeURIComponent(period)}`);
    renderTracks(element('chart-tracks'), overview.tracks);
    const statistics = overview.statistics;
    element('listening-stats').textContent = `${statistics.scrobbles.toLocaleString()} scrobbles · ${statistics.artists.toLocaleString()} artists · ${statistics.albums.toLocaleString()} albums`;
    for (const [id, entries] of [['chart-artists', overview.artists], ['chart-albums', overview.albums]] as const) {
      const list = element(id); list.replaceChildren();
      for (const item of entries.slice(0, 50)) {
        const row = document.createElement('div');
        line(row, `${item.name} · ${item.playCount.toLocaleString()} plays`);
        if (item.url) link(row, 'Last.fm ↗', item.url, true);
        list.append(row);
      }
    }
    message('Listening charts loaded.');
  });
  setupHistoryImport(client);
  bind('discovery-refresh', async () => {
    const seed = element<HTMLSelectElement>('discovery-seed').value.trim();
    const result = await client.api<{ local: Match[]; external: Track[]; artists: DiscoveryEntity[]; albums: DiscoveryEntity[] }>(`Me/Discovery${seed ? `?seedItemId=${encodeURIComponent(seed)}` : ''}`);
    renderTracks(element('discovery-local'), result.local.map(match => match.track), client, result.local);
    renderTracks(element('discovery-external'), result.external);
    renderEntities(element('discovery-artists'), result.artists, client);
    renderEntities(element('discovery-albums'), result.albums, client);
    message('Discovery refreshed.');
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
    if (!preview) throw new Error('Create a fresh preview first.');
    await client.api('Me/History/Import', 'POST', { previewId: preview.id });
    render(undefined);
    message('Previewed play counts imported. Existing higher counts were retained.');
  });
}

function showPreview(preview: Preview | undefined): void {
  const output = element('history-preview-list'); output.replaceChildren();
  element<HTMLButtonElement>('history-apply').disabled = !preview?.matchedCount;
  element('history-continue').hidden = !preview || preview.complete;
  element('preview-pagination').hidden = !preview;
  if (!preview) { message('No saved preview. Prepare a new preview to import history.'); return; }
  line(output, `${preview.matchedCount} matched tracks; ${preview.unmatchedCount} unmatched or ambiguous.`);
  line(output, 'Counts use the higher total, not a sum. Separate listening histories may be undercounted.');
  for (const entry of preview.entries) {
    line(output, `${entry.track.artist} — ${entry.track.title}: ${entry.currentPlayCount} → ${entry.proposedPlayCount}`);
    if (entry.proposedLastPlayed) line(output, `Last played: ${entry.currentLastPlayed ? new Date(entry.currentLastPlayed).toLocaleString() : 'unknown'} → ${new Date(entry.proposedLastPlayed).toLocaleString()}`, 'detail');
  }
  for (const match of preview.unmatched) line(output, `${match.track.artist} — ${match.track.title}: ${match.status}; skipped`, 'muted');
  if (!preview.complete) line(output, 'This preview is incomplete. Continue gathering history, or import only the matched tracks gathered so far.');
  element('preview-page').textContent = `Preview page ${preview.page} of ${preview.pages}`;
  element<HTMLButtonElement>('preview-previous').disabled = preview.page === 1;
  element<HTMLButtonElement>('preview-next').disabled = preview.page >= preview.pages;
  message('Review the play-count changes before importing.');
}

function renderEntities(parent: HTMLElement, entries: DiscoveryEntity[], client: Client): void {
  parent.replaceChildren();
  for (const entry of entries.slice(0, 100)) {
    const row = document.createElement('li');
    line(row, `${entry.name}${entry.artist ? ` · ${entry.artist}` : ''}`);
    if (entry.itemId) link(row, 'Open in Jellyfin', `${client.root}/web/index.html#!/details?id=${encodeURIComponent(entry.itemId)}&serverId=${encodeURIComponent(client.config.serverId)}`);
    if (entry.url) link(row, 'Last.fm ↗', entry.url, true);
    parent.append(row);
  }
  if (!entries.length) line(parent, 'No discoveries yet.', 'muted');
}
