import { Client } from './client.js';
import { bind, delegate, element, line, link, message, rowButton, run } from './view.js';
import { t } from './i18n.js';

interface Recipe { id: string; name: string; source: number | string; period: string; seedItemId?: string; limit: number; dailyRefresh: boolean; playlistId?: string; retentionDays?: number }
interface Pending { operationId: string; recipeId: string; name: string; playlistId?: string; itemCount: number; status: string }

export function setupPlaylists(client: Client): void {
  const shown = new Map<string, Recipe>();
  let pending: Pending | undefined;
  setupRotation(element<HTMLSelectElement>('playlist-source'), element('playlist-rotate-group'), element<HTMLInputElement>('playlist-rotate'));

  const refresh = async (): Promise<void> => {
    const [recipes, current] = await Promise.all([client.api<Recipe[]>('Me/Playlists'), client.api<Pending | undefined>('Me/Playlists/Pending')]);
    pending = current;
    showPending(pending);
    shown.clear();
    const list = element('playlist-list'); list.replaceChildren();
    if (!recipes.length) line(list, t('playlists_empty'), 'muted');
    for (const recipe of recipes.slice(0, 100)) {
      shown.set(recipe.id, recipe);
      const item = document.createElement('li');
      line(item, recipe.name);
      line(item, `${t('playlists_tracks_count', { limit: recipe.limit })}${recipe.dailyRefresh ? t('playlists_refreshes_daily') : ''}${recipe.retentionDays ? t('playlists_rotates_days', { days: recipe.retentionDays }) : ''}`, 'muted');
      if (recipe.playlistId) link(item, t('playlists_open'), `${client.root}/web/index.html#/details?id=${encodeURIComponent(recipe.playlistId)}&serverId=${encodeURIComponent(client.config.serverId)}`);
      item.append(rowButton(t('playlists_regenerate'), { action: 'regenerate', recipe: recipe.id }), rowButton(t('playlists_stop_managing'), { action: 'stop', recipe: recipe.id }));
      list.append(item);
    }
  };
  delegate(element('playlist-list'), async control => {
    const recipe = shown.get(control.dataset.recipe ?? '');
    if (!recipe) return;
    if (control.dataset.action === 'stop') {
      await client.api(`Me/Playlists/${encodeURIComponent(recipe.id)}`, 'DELETE');
      await refresh(); message(t('playlists_stop_notice'));
      return;
    }
    try {
      await client.api('Me/Playlists', 'POST', { id: recipe.id, name: recipe.name, source: recipe.source,
        period: recipe.period, seedItemId: recipe.seedItemId, limit: recipe.limit, dailyRefresh: recipe.dailyRefresh,
        retentionDays: recipe.retentionDays ?? 0 });
    } catch (error) { await refresh(); throw error; }
    await refresh(); message(t('playlists_regenerated'));
  });
  delegate(element('playlist-pending'), async control => {
    if (!pending) return;
    if (control.dataset.action === 'review') { confirmCancellation(); return; }
    if (control.dataset.action === 'keep') { showPending(pending); return; }
    await client.api(`Me/Playlists/Pending/${encodeURIComponent(pending.operationId)}`, 'DELETE');
    await refresh(); message(t('playlists_pending_cancelled'));
  });
  bind('playlists-refresh', refresh);
  bindPlaylistForm(client, refresh);
}

function setupRotation(source: HTMLSelectElement, group: HTMLElement, input: HTMLInputElement): void {
  const update = (): void => {
    const isRotating = source.value === '2' || source.value === '3';
    group.hidden = !isRotating;
    input.disabled = !isRotating;
    if (!isRotating) input.value = '0';
  };
  source.addEventListener('change', update);
  update();
}

function bindPlaylistForm(client: Client, refresh: () => Promise<void>): void {
  const source = element<HTMLSelectElement>('playlist-source');
  const rotateInput = element<HTMLInputElement>('playlist-rotate');
  element<HTMLFormElement>('playlist-form').addEventListener('submit', event => {
    event.preventDefault();
    void run(element<HTMLButtonElement>('playlist-create'), async () => {
      const seed = element<HTMLInputElement>('playlist-seed').value.trim();
      const isRotating = source.value === '2' || source.value === '3';
      try {
        await client.api('Me/Playlists', 'POST', {
          id: '00000000-0000-0000-0000-000000000000', name: element<HTMLInputElement>('playlist-name').value,
          source: Number(source.value), period: element<HTMLSelectElement>('playlist-period').value,
          seedItemId: seed || null, limit: Number(element<HTMLInputElement>('playlist-limit').value),
          dailyRefresh: element<HTMLInputElement>('playlist-daily').checked,
          retentionDays: isRotating ? Number(rotateInput.value) || 0 : 0,
        });
      } catch (error) { await refresh(); throw error; }
      await refresh(); message(t('playlists_created'));
    });
  });
}

function showPending(pending: Pending | undefined): void {
  const output = element('playlist-pending'); output.replaceChildren();
  if (!pending) return;
  line(output, t('playlists_interrupted', { name: pending.name, count: pending.itemCount }));
  line(output, pending.status);
  output.append(rowButton(t('playlists_review_cancellation'), { action: 'review' }));
}

function confirmCancellation(): void {
  const confirmation = document.createElement('div');
  line(confirmation, t('playlists_cancellation_confirm'));
  const apply = rowButton(t('playlists_cancel_and_pause'), { action: 'cancel' });
  confirmation.append(apply, rowButton(t('playlists_keep_waiting'), { action: 'keep' }));
  element('playlist-pending').replaceChildren(confirmation); apply.focus();
}
