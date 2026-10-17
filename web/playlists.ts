import { Client } from './client.js';
import { bind, delegate, element, line, link, message, rowButton, run } from './view.js';

interface Recipe { id: string; name: string; source: number | string; period: string; seedItemId?: string; limit: number; dailyRefresh: boolean; playlistId?: string }
interface Pending { operationId: string; recipeId: string; name: string; playlistId?: string; itemCount: number; status: string }

export function setupPlaylists(client: Client): void {
  const shown = new Map<string, Recipe>();
  let pending: Pending | undefined;
  const refresh = async (): Promise<void> => {
    const [recipes, current] = await Promise.all([client.api<Recipe[]>('Me/Playlists'), client.api<Pending | undefined>('Me/Playlists/Pending')]);
    pending = current;
    showPending(pending);
    shown.clear();
    const list = element('playlist-list'); list.replaceChildren();
    if (!recipes.length) line(list, 'Create a playlist to get started.', 'muted');
    for (const recipe of recipes.slice(0, 100)) {
      shown.set(recipe.id, recipe);
      const item = document.createElement('li');
      line(item, recipe.name);
      line(item, `${recipe.limit} tracks${recipe.dailyRefresh ? ' · refreshes daily' : ''}`, 'muted');
      if (recipe.playlistId) link(item, 'Open playlist', `${client.root}/web/index.html#/details?id=${encodeURIComponent(recipe.playlistId)}&serverId=${encodeURIComponent(client.config.serverId)}`);
      item.append(rowButton('Regenerate', { action: 'regenerate', recipe: recipe.id }), rowButton('Stop managing', { action: 'stop', recipe: recipe.id }));
      list.append(item);
    }
  };
  delegate(element('playlist-list'), async control => {
    const recipe = shown.get(control.dataset.recipe ?? '');
    if (!recipe) return;
    if (control.dataset.action === 'stop') {
      await client.api(`Me/Playlists/${encodeURIComponent(recipe.id)}`, 'DELETE');
      await refresh(); message('Automatic management stopped. The Jellyfin playlist was kept.');
      return;
    }
    try {
      await client.api('Me/Playlists', 'POST', { id: recipe.id, name: recipe.name, source: recipe.source,
        period: recipe.period, seedItemId: recipe.seedItemId, limit: recipe.limit, dailyRefresh: recipe.dailyRefresh });
    } catch (error) { await refresh(); throw error; }
    await refresh(); message('Playlist regenerated.');
  });
  delegate(element('playlist-pending'), async control => {
    if (!pending) return;
    if (control.dataset.action === 'review') { confirmCancellation(); return; }
    if (control.dataset.action === 'keep') { showPending(pending); return; }
    await client.api(`Me/Playlists/Pending/${encodeURIComponent(pending.operationId)}`, 'DELETE');
    await refresh(); message('Pending update cancelled. The playlist was kept and daily refresh paused.');
  });
  bind('playlists-refresh', refresh);
  element<HTMLFormElement>('playlist-form').addEventListener('submit', event => {
    event.preventDefault();
    void run(element<HTMLButtonElement>('playlist-create'), async () => {
      const seed = element<HTMLInputElement>('playlist-seed').value.trim();
      try {
        await client.api('Me/Playlists', 'POST', {
          id: '00000000-0000-0000-0000-000000000000', name: element<HTMLInputElement>('playlist-name').value,
          source: Number(element<HTMLSelectElement>('playlist-source').value), period: element<HTMLSelectElement>('playlist-period').value,
          seedItemId: seed || null, limit: Number(element<HTMLInputElement>('playlist-limit').value),
          dailyRefresh: element<HTMLInputElement>('playlist-daily').checked,
        });
      } catch (error) { await refresh(); throw error; }
      await refresh(); message('Private Jellyfin playlist created.');
    });
  });
}

function showPending(pending: Pending | undefined): void {
  const output = element('playlist-pending'); output.replaceChildren();
  if (!pending) return;
  line(output, `Interrupted update: ${pending.name} · ${pending.itemCount} intended tracks`);
  line(output, pending.status);
  output.append(rowButton('Review cancellation', { action: 'review' }));
}

function confirmCancellation(): void {
  const confirmation = document.createElement('div');
  line(confirmation, 'Cancel this update and pause its daily refresh? The Jellyfin playlist will remain in its current, possibly partial state.');
  const apply = rowButton('Cancel update and pause refresh', { action: 'cancel' });
  confirmation.append(apply, rowButton('Keep waiting', { action: 'keep' }));
  element('playlist-pending').replaceChildren(confirmation); apply.focus();
}
