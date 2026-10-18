import { Client } from './client.js';
import { bind, delegate, element, line, message, rowButton } from './view.js';
import { type Track } from './music.js';
import { t } from './i18n.js';

interface Review { enabled: boolean; pending: { id: string; track: Track; removeFrom: string }[]; lastSync?: string; status: string; total: number; page: number; pages: number; running: boolean }

export function setupFavourites(client: Client): void {
  let page = 1;
  const render = (review: Review): void => {
    page = review.page;
    element<HTMLInputElement>('favourites-enabled').checked = review.enabled;
    element('favourites-status').textContent = review.running
      ? t('favourites_running')
      : review.status;
    element('favourites-page').textContent = t('favourites_page', { total: review.total, page: review.page, pages: review.pages });
    element<HTMLButtonElement>('favourites-previous').disabled = page === 1;
    element<HTMLButtonElement>('favourites-next').disabled = page >= review.pages;
    const list = element('removal-list'); list.replaceChildren();
    if (!review.pending.length) line(list, t('favourites_no_removals'), 'muted');
    for (const removal of review.pending.slice(0, 200)) {
      const item = document.createElement('li');
      line(item, `${removal.track.artist} — ${removal.track.title}`);
      line(item, t('favourites_removal_from', { service: removal.removeFrom === 'lastfm' ? 'Last.fm' : 'Jellyfin' }), 'muted');
      for (const apply of [true, false]) item.append(rowButton(apply ? t('favourites_apply_removal') : t('favourites_keep_unchanged'), { action: String(apply), review: removal.id }));
      list.append(item);
    }
  };
  delegate(element('removal-list'), async control => {
    const apply = control.dataset.action === 'true';
    render(await client.api<Review>('Me/Favourites/Review', 'POST', { reviewId: control.dataset.review, apply }));
    message(apply ? t('favourites_reviewed_applied') : t('favourites_dismissed'));
  });
  bind('favourites-save', async () => {
    render(await client.api<Review>('Me/Favourites', 'PUT', { enabled: element<HTMLInputElement>('favourites-enabled').checked }));
    message(t('favourites_preference_saved'));
  });
  bind('favourites-refresh', async () => { render(await client.api<Review>('Me/Favourites')); });
  bind('favourites-previous', async () => { render(await client.api<Review>(`Me/Favourites?page=${Math.max(1, page - 1)}`)); });
  bind('favourites-next', async () => { render(await client.api<Review>(`Me/Favourites?page=${page + 1}`)); });
  bind('favourites-sync', async () => {
    const review = await client.api<Review>('Me/Favourites/Sync', 'POST');
    render(review);
    message(review.running ? t('favourites_sync_continuing') : t('favourites_synced'));
  });
}
