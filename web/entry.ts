interface ApiClientGlobal {
  getCurrentUserId?: () => string | null | undefined;
}

declare global {
  interface Window {
    ApiClient?: ApiClientGlobal;
  }
}

interface LastfmHistoryState {
  jellysinLastfmOpen?: boolean;
}

let warnedLayout = false;
let activeDialog: HTMLElement | null = null;

function warnLayout(): void {
  if (!warnedLayout) {
    warnedLayout = true;
    // eslint-disable-next-line no-console
    console.warn('JellySin Last.fm: user settings layout not recognised');
  }
}

function getBase(): string {
  // A module URL is always absolute, so it parses.
  const pathname = new URL(import.meta.url).pathname;
  const index = pathname.indexOf('/JellySin/Lastfm/Assets/entry.js');
  return index >= 0 ? pathname.slice(0, index) : '';
}

function getHashUserId(): string | null {
  const hash = window.location.hash || '';
  const queryIndex = hash.indexOf('?');
  if (queryIndex < 0) {
    return null;
  }
  const params = new URLSearchParams(hash.slice(queryIndex));
  return params.get('userId');
}

/** An administrator opening another user's settings sees `?userId=`; an unknown current user counts as another user. */
function isOwnSettings(): boolean {
  const hashUserId = getHashUserId();
  return hashUserId === null || hashUserId === window.ApiClient?.getCurrentUserId?.();
}

function isOurStateCurrent(): boolean {
  const state = window.history.state as LastfmHistoryState | null;
  return Boolean(state?.jellysinLastfmOpen);
}

function handleKeyDown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault();
    closeView(false);
  }
}

function destroyView(): void {
  if (!activeDialog) return;
  const dialog = activeDialog;
  activeDialog = null;
  dialog.remove();
  window.removeEventListener('keydown', handleKeyDown);
}

function closeView(fromPopState = false): void {
  if (!activeDialog) return;
  destroyView();
  if (!fromPopState && isOurStateCurrent()) {
    window.history.back();
  }
}

function createCloseButton(): HTMLButtonElement {
  const closeButton = document.createElement('button');
  closeButton.type = 'button';
  closeButton.setAttribute('aria-label', 'Close');
  closeButton.textContent = '×';
  closeButton.className = 'jellysin-lastfm-close';
  closeButton.style.position = 'absolute';
  closeButton.style.top = '10px';
  closeButton.style.right = '16px';
  closeButton.style.zIndex = '10';
  closeButton.style.fontSize = '28px';
  closeButton.style.lineHeight = '1';
  closeButton.style.width = '36px';
  closeButton.style.height = '36px';
  closeButton.style.cursor = 'pointer';
  closeButton.style.background = 'rgba(0, 0, 0, 0.6)';
  closeButton.style.color = '#fff';
  closeButton.style.border = '1px solid rgba(255, 255, 255, 0.2)';
  closeButton.style.borderRadius = '50%';
  closeButton.addEventListener('click', () => closeView(false));
  return closeButton;
}

function openView(base: string, failed = false): void {
  if (!document.body || activeDialog) return;

  const header = document.querySelector('.skinHeader');
  const topOffset = header ? header.getBoundingClientRect().height : 0;

  const container = document.createElement('div');
  container.className = 'jellysin-lastfm-dialog';
  container.setAttribute('role', 'dialog');
  container.setAttribute('aria-label', 'Last.fm');
  container.style.position = 'fixed';
  container.style.top = `${topOffset}px`;
  container.style.left = '0';
  container.style.right = '0';
  container.style.bottom = '0';
  container.style.width = '100%';
  container.style.height = `calc(100% - ${topOffset}px)`;
  container.style.zIndex = '1000';
  container.style.backgroundColor = '#101010';

  const iframe = document.createElement('iframe');
  iframe.title = 'Last.fm';
  iframe.src = `${base}/JellySin/Lastfm/?embedded=1${failed ? '&connection=failed' : ''}`;
  iframe.style.width = '100%';
  iframe.style.height = '100%';
  iframe.style.border = 'none';

  container.appendChild(createCloseButton());
  container.appendChild(iframe);
  document.body.appendChild(container);
  activeDialog = container;

  window.addEventListener('keydown', handleKeyDown);

  // Back closes the view; the address stays the same, so the web client's router sees no navigation.
  const current = (window.history.state ?? {}) as Record<string, unknown>;
  window.history.pushState({ ...current, jellysinLastfmOpen: true }, '', window.location.href);
}

function checkAutoOpen(base: string): void {
  const hash = window.location.hash || '';
  const queryIndex = hash.indexOf('?');
  const route = queryIndex >= 0 ? hash.slice(0, queryIndex) : hash;
  if (route !== '#/mypreferencesmenu') return;

  const params = new URLSearchParams(queryIndex >= 0 ? hash.slice(queryIndex) : '');
  if (params.get('lastfm') !== 'open' || !isOwnSettings()) return;

  const failed = params.get('connection') === 'failed';
  params.delete('lastfm');
  params.delete('connection');
  const remaining = params.toString();
  const newHash = route + (remaining ? `?${remaining}` : '');
  const newUrl = window.location.pathname + window.location.search + newHash;
  window.history.replaceState(window.history.state, '', newUrl);
  openView(base, failed);
}

function createRow(base: string): HTMLAnchorElement {
  const a = document.createElement('a');
  a.className = 'lnkJellySinLastfm listItem-border emby-button';
  a.style.display = 'block';
  a.style.margin = '0';
  a.style.padding = '0';
  a.href = `${base}/JellySin/Lastfm/`;
  a.innerHTML = '<div class="listItem"><span class="material-icons listItemIcon listItemIcon-transparent music_note"></span><div class="listItemBody"><div class="listItemBodyText">Last.fm</div></div></div>';
  a.addEventListener('click', event => {
    event.preventDefault();
    openView(base);
  });
  return a;
}

function findAnchor(page: Element): Element | null {
  const playback = page.querySelector('a.lnkPlaybackPreferences');
  if (playback) {
    return playback;
  }
  const firstSection = page.querySelector('.verticalSection');
  if (firstSection) {
    const links = firstSection.querySelectorAll('a.listItem-border');
    if (links.length > 0) {
      return links[links.length - 1] ?? null;
    }
  }
  return null;
}

function handlePage(page: Element): void {
  if (!isOwnSettings()) {
    return;
  }

  const anchor = findAnchor(page);
  if (!anchor) {
    warnLayout();
    return;
  }

  const base = getBase();
  const row = createRow(base);
  anchor.after(row);
}

function scan(): void {
  // The settings page re-renders its rows, so presence of the row is checked rather than a mark on the page.
  for (const page of document.querySelectorAll('.userPreferencesPage')) {
    if (!page.querySelector('a.lnkJellySinLastfm')) {
      handlePage(page);
    }
  }
  checkAutoOpen(getBase());
}

window.addEventListener('popstate', () => {
  if (activeDialog) closeView(true);
});

window.addEventListener('hashchange', () => {
  checkAutoOpen(getBase());
});

const observer = new MutationObserver(() => {
  scan();
});

if (document.body) {
  observer.observe(document.body, { childList: true, subtree: true });
  scan();
} else {
  document.addEventListener('DOMContentLoaded', () => {
    if (document.body) {
      observer.observe(document.body, { childList: true, subtree: true });
      scan();
    }
  });
}

export {};
