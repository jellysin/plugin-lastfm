interface ApiClientGlobal {
  getCurrentUserId?: () => string | null | undefined;
}

declare global {
  interface Window {
    ApiClient?: ApiClientGlobal;
  }
}

let warnedLayout = false;

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

function createRow(base: string): HTMLAnchorElement {
  const a = document.createElement('a');
  a.className = 'lnkJellySinLastfm listItem-border emby-button';
  a.style.display = 'block';
  a.style.margin = '0';
  a.style.padding = '0';
  a.href = `${base}/JellySin/Lastfm/`;
  a.innerHTML = '<div class="listItem"><span class="material-icons listItemIcon listItemIcon-transparent music_note"></span><div class="listItemBody"><div class="listItemBodyText">Last.fm</div></div></div>';
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
  const hashUserId = getHashUserId();
  const currentUserId = window.ApiClient?.getCurrentUserId?.();
  if (hashUserId !== null && hashUserId !== currentUserId) {
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
}

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
