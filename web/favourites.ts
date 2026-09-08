import { Client } from './client.js';
import { bind, button, element, line, message } from './view.js';
import { type Track } from './music.js';

interface Review { enabled: boolean; pending: { id: string; track: Track; removeFrom: string }[]; lastSync?: string; status: string; total: number; page: number; pages: number }

export function setupFavourites(client: Client): void {
  let page = 1;
  const render = (review: Review): void => {
    page = review.page;
    element<HTMLInputElement>('favourites-enabled').checked = review.enabled;
    element('favourites-status').textContent = review.status;
    element('favourites-page').textContent = `${review.total} pending · Page ${review.page} of ${review.pages}`;
    element<HTMLButtonElement>('favourites-previous').disabled = page === 1;
    element<HTMLButtonElement>('favourites-next').disabled = page >= review.pages;
    const list = element('removal-list'); list.replaceChildren();
    if (!review.pending.length) line(list, 'No removals waiting for review.', 'muted');
    for (const removal of review.pending.slice(0, 200)) {
      const item = document.createElement('li');
      line(item, `${removal.track.artist} — ${removal.track.title}`);
      line(item, `Remove the favourite from ${removal.removeFrom === 'lastfm' ? 'Last.fm' : 'Jellyfin'}?`, 'muted');
      for (const apply of [true, false]) item.append(button(apply ? 'Apply removal' : 'Keep unchanged', async () => {
        render(await client.api<Review>('Me/Favourites/Review', 'POST', { reviewId: removal.id, apply }));
        message(apply ? 'Removal applied after revalidation.' : 'Removal dismissed.');
      }));
      list.append(item);
    }
  };
  bind('favourites-save', async () => {
    render(await client.api<Review>('Me/Favourites', 'PUT', { enabled: element<HTMLInputElement>('favourites-enabled').checked }));
    message('Favourite synchronization preference saved.');
  });
  bind('favourites-refresh', async () => { render(await client.api<Review>('Me/Favourites')); });
  bind('favourites-previous', async () => { render(await client.api<Review>(`Me/Favourites?page=${Math.max(1, page - 1)}`)); });
  bind('favourites-next', async () => { render(await client.api<Review>(`Me/Favourites?page=${page + 1}`)); });
  bind('favourites-sync', async () => { render(await client.api<Review>('Me/Favourites/Sync', 'POST')); message('Favourites synchronized.'); });
}
