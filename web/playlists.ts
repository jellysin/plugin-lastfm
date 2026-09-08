import { Client } from './client.js';
import { bind, button, element, line, link, message, run } from './view.js';

interface Recipe { id: string; name: string; source: number | string; period: string; seedItemId?: string; limit: number; dailyRefresh: boolean; playlistId?: string }

export function setupPlaylists(client: Client): void {
  const refresh = async (): Promise<void> => {
    const recipes = await client.api<Recipe[]>('Me/Playlists');
    const list = element('playlist-list'); list.replaceChildren();
    if (!recipes.length) line(list, 'Create a playlist to get started.', 'muted');
    for (const recipe of recipes.slice(0, 100)) {
      const item = document.createElement('li');
      line(item, recipe.name);
      line(item, `${recipe.limit} tracks${recipe.dailyRefresh ? ' · refreshes daily' : ''}`, 'muted');
      if (recipe.playlistId) link(item, 'Open playlist', `${client.root}/web/index.html#!/details?id=${encodeURIComponent(recipe.playlistId)}&serverId=${encodeURIComponent(client.config.serverId)}`);
      item.append(button('Regenerate', async () => {
        await client.api('Me/Playlists', 'POST', { id: recipe.id, name: recipe.name, source: recipe.source,
          period: recipe.period, seedItemId: recipe.seedItemId, limit: recipe.limit, dailyRefresh: recipe.dailyRefresh });
        await refresh(); message('Playlist regenerated.');
      }));
      item.append(button('Stop managing', async () => {
        await client.api(`Me/Playlists/${encodeURIComponent(recipe.id)}`, 'DELETE');
        await refresh(); message('Automatic management stopped. The Jellyfin playlist was kept.');
      }));
      list.append(item);
    }
  };
  bind('playlists-refresh', refresh);
  element<HTMLFormElement>('playlist-form').addEventListener('submit', event => {
    event.preventDefault();
    void run(element<HTMLButtonElement>('playlist-create'), async () => {
      const seed = element<HTMLInputElement>('playlist-seed').value.trim();
      await client.api('Me/Playlists', 'POST', {
        id: '00000000-0000-0000-0000-000000000000', name: element<HTMLInputElement>('playlist-name').value,
        source: Number(element<HTMLSelectElement>('playlist-source').value), period: element<HTMLSelectElement>('playlist-period').value,
        seedItemId: seed || null, limit: Number(element<HTMLInputElement>('playlist-limit').value),
        dailyRefresh: element<HTMLInputElement>('playlist-daily').checked,
      });
      await refresh(); message('Private Jellyfin playlist created.');
    });
  });
}
