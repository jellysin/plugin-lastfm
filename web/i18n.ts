import { catalogs } from './catalogs.js';

interface ResolveOptions {
  hostLang?: string | null;
  languages?: readonly string[];
  isAdmin?: boolean;
}

function isAdminPage(): boolean {
  return typeof document !== 'undefined' && Boolean(document.getElementById('jellysin-admin'));
}

function matchLocale(tag: string): string | undefined {
  const primary = tag.trim().split(/[-_]/)[0]?.toLowerCase();
  if (primary && Object.prototype.hasOwnProperty.call(catalogs, primary)) {
    return primary;
  }
  return undefined;
}

export function resolveLocale(options?: ResolveOptions): string {
  const isAdmin = options?.isAdmin ?? isAdminPage();
  const hostLang = options?.hostLang !== undefined
    ? options.hostLang
    : (isAdmin && typeof document !== 'undefined'
      ? (document.documentElement.getAttribute('lang') || document.documentElement.lang)
      : '');

  if (isAdmin && hostLang && hostLang.trim()) {
    const match = matchLocale(hostLang);
    if (match) return match;
    return 'en';
  }

  let candidates: readonly string[] = [];
  if (options?.languages) {
    candidates = options.languages;
  } else if (typeof navigator !== 'undefined') {
    if (Array.isArray(navigator.languages) && navigator.languages.length > 0) {
      candidates = navigator.languages;
    } else if (typeof navigator.language === 'string') {
      candidates = [navigator.language];
    }
  }

  const limit = Math.min(candidates.length, 32);
  for (let i = 0; i < limit; i++) {
    const candidate = candidates[i];
    if (typeof candidate === 'string' && candidate.trim()) {
      const match = matchLocale(candidate);
      if (match) return match;
    }
  }

  return 'en';
}

let activeLocale = 'en';

export function getLocale(): string {
  return activeLocale;
}

export function setLocale(locale: string): void {
  activeLocale = Object.prototype.hasOwnProperty.call(catalogs, locale) ? locale : 'en';
}

export function t(key: string, params?: Record<string, string | number>): string {
  const catalog = catalogs[activeLocale] ?? catalogs.en;
  let text = catalog?.[key] ?? catalogs.en?.[key] ?? key;
  if (params && typeof params === 'object') {
    const entries = Object.entries(params).slice(0, 32);
    for (let i = 0; i < entries.length; i++) {
      const entry = entries[i];
      if (entry) {
        text = text.replaceAll(`{${entry[0]}}`, String(entry[1]));
      }
    }
  }
  return text;
}

export function applyTranslations(root: ParentNode = document): void {
  activeLocale = resolveLocale();
  if (root instanceof Document && root.documentElement && !isAdminPage()) {
    root.documentElement.lang = activeLocale;
  }
  const elements = root.querySelectorAll<HTMLElement>('[data-i18n]');
  const limit = Math.min(elements.length, 2048);
  for (let i = 0; i < limit; i++) {
    const el = elements[i];
    if (!el) continue;
    const key = el.dataset.i18n;
    if (!key) continue;
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
      el.placeholder = t(key);
    } else if (key.endsWith('_aria') || (el.hasAttribute('aria-label') && (el.textContent === null || el.textContent.trim() === ''))) {
      el.setAttribute('aria-label', t(key));
    } else {
      el.textContent = t(key);
    }
  }
}
