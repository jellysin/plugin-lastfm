import { t } from './i18n.js';

export interface Bootstrap { basePath: string; serverId: string; version: string }
export interface Session { accessToken: string; user: { name: string }; serverId: string }

/** The server marks the conflict "no Last.fm account connected" with this value (ApiExceptionFilter.NotConnectedType). */
export const notConnectedType = 'urn:jellysin:lastfm:not-connected';
/** The server marks a request refused for lack of a usable Last.fm application with this value (ApiExceptionFilter.ApplicationMissingType). */
export const applicationMissingType = 'urn:jellysin:lastfm:application-missing';

/** Reads per response; a server that sends many empty chunks stops here as well. */
const maxReads = 65_536;
/** Values per response; a 4 MB answer cannot hold more. */
const maxValues = 4_000_000;

/** Stop reading as soon as the byte budget is exhausted, including chunked responses. */
export async function readJson(response: Response, limit = 4_000_000): Promise<unknown> {
  const reader = response.body?.getReader();
  if (!reader) throw new Error(t('client_empty_response'));
  const decoder = new TextDecoder('utf-8', { fatal: true });
  const parts: string[] = [];
  let bytes = 0;
  try {
    let done = false;
    for (let reads = 0; !done && reads < maxReads; reads++) {
      const chunk = await reader.read();
      done = chunk.done;
      if (chunk.done) break;
      bytes += chunk.value.byteLength;
      if (bytes > limit) throw new Error(t('client_response_too_large'));
      parts.push(decoder.decode(chunk.value, { stream: true }));
    }
    if (!done) throw new Error(t('client_response_too_large'));
    parts.push(decoder.decode());
    return normalize(JSON.parse(parts.join('')) as unknown);
  } finally { await reader.cancel(); reader.releaseLock(); }
}

interface Pending { source: unknown; depth: number; place: (value: unknown) => void }

/** JSON from either Jellyfin serializer profile is normalized with a fixed nesting bound. */
export function normalize(value: unknown): unknown {
  let result: unknown;
  const pending: Pending[] = [{ source: value, depth: 0, place: normalized => { result = normalized; } }];
  for (let visited = 0; pending.length > 0 && visited < maxValues; visited++) {
    const next = pending.pop();
    if (next) next.place(shallow(next.source, next.depth, pending));
  }
  if (pending.length > 0) throw new Error(t('client_response_too_large'));
  return result;
}

/** One level of the walk: containers are created here and their entries are queued for the next steps. */
function shallow(value: unknown, depth: number, pending: Pending[]): unknown {
  if (depth > 16) throw new Error(t('client_response_too_deep'));
  if (Array.isArray(value)) {
    if (value.length > 20_000) throw new Error(t('client_response_too_large'));
    const list: unknown[] = new Array<unknown>(value.length);
    value.forEach((entry: unknown, index: number) => pending.push({ source: entry, depth: depth + 1, place: normalized => { list[index] = normalized; } }));
    return list;
  }
  if (value !== null && typeof value === 'object') {
    const record: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
    // A later key that differs only in its first letter's case replaces the earlier value, as plain assignment does.
    const entries = new Map<string, unknown>();
    for (const [key, entry] of Object.entries(value).slice(0, 200)) {
      if (['__proto__', 'constructor', 'prototype'].includes(key)) continue;
      entries.set(key.charAt(0).toLowerCase() + key.slice(1), entry);
    }
    for (const [key, entry] of entries) {
      record[key] = undefined;
      pending.push({ source: entry, depth: depth + 1, place: normalized => { record[key] = normalized; } });
    }
    return record;
  }
  return value;
}

/** Whether an error answer carries the given mark. Only the mark is compared; no text of the answer is shown. */
async function marked(response: Response, type: string): Promise<boolean> {
  try { return (await readJson(response, 4096) as { type?: unknown } | null)?.type === type; }
  catch { return false; }
}

export class Client extends EventTarget {
  private token = '';
  /** Whether browser storage matches the session in memory; false when storage refused the last write. */
  storageCurrent = true;
  private sessionRequests = new AbortController();
  private readonly deviceId = Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('');
  private readonly storageKey: string;
  readonly root: string;

  constructor(readonly config: Bootstrap, private readonly storage: Storage | undefined) {
    super();
    if (!/^\/(?:[^?#\\\r\n]*)$/.test(config.basePath || '/') || config.basePath.startsWith('//')) {
      throw new Error(t('client_base_path_invalid'));
    }
    this.root = config.basePath.replace(/\/$/, '');
    this.storageKey = `jellysin:${config.serverId}:${this.root}`;
    try { this.token = storage?.getItem(this.storageKey) ?? ''; } catch { this.token = ''; }
    if (!/^[a-f0-9]{32,128}$/i.test(this.token)) this.token = '';
  }

  get signedIn(): boolean { return this.token.length > 0; }

  acceptSession(session: Session): void {
    if (!/^[a-f0-9]{32,128}$/i.test(session.accessToken) || session.serverId !== this.config.serverId) {
      throw new Error(t('client_invalid_session'));
    }
    if (this.token) this.clearSession();
    this.sessionRequests.abort();
    this.sessionRequests = new AbortController();
    this.token = session.accessToken;
    try { this.storage?.setItem(this.storageKey, this.token); this.storageCurrent = true; } catch { this.storageCurrent = false; /* Memory-only sign-in remains available. */ }
  }

  clearSession(): void {
    this.resetAccount();
    this.token = '';
    try { this.storage?.removeItem(this.storageKey); this.storageCurrent = true; } catch { this.storageCurrent = false; /* Storage may be disabled. */ }
    this.dispatchEvent(new Event('sessioncleared'));
  }

  resetAccount(): void {
    this.sessionRequests.abort();
    this.sessionRequests = new AbortController();
    this.dispatchEvent(new Event('accountchanged'));
  }

  async logout(): Promise<void> {
    const authorization = this.authorizationHeader();
    this.clearSession();
    try {
      const response = await fetch(this.root + '/Sessions/Logout', {
        method: 'POST', credentials: 'omit', redirect: 'error', cache: 'no-store',
        headers: { Authorization: authorization }, signal: AbortSignal.timeout(15_000),
      });
      if (!response.ok && response.status !== 401) throw new Error(t('client_revocation_failed'));
    } catch {
      if (!this.signedIn) throw new Error(t('client_signed_out_unconfirmed'));
    }
  }

  private authorizationHeader(): string {
    const header = `MediaBrowser Client="JellySin%20Last.fm", Device="Browser", DeviceId="${this.deviceId}", Version="${encodeURIComponent(this.config.version)}"`;
    return header + (this.token ? `, Token="${this.token}"` : '');
  }

  async api<T>(path: string, method = 'GET', body?: unknown, signal?: AbortSignal, cookies = false): Promise<T> {
    return this.request<T>(`/JellySin/Lastfm/${path}`, method, body, signal, cookies);
  }

  /** Requests carry no browser cookies. The start of a Last.fm sign-in is the exception: its response sets the cookie that ties the return to this browser. */
  async request<T>(path: string, method = 'GET', body?: unknown, signal?: AbortSignal, cookies = false): Promise<T> {
    if (!path.startsWith('/') || path.startsWith('//') || /[\r\n\\]/.test(path)) throw new Error('Invalid API path.');
    const requestSignal = AbortSignal.any([this.sessionRequests.signal, AbortSignal.timeout(90_000), ...(signal ? [signal] : [])]);
    const response = await fetch(this.root + path, {
      method, credentials: cookies ? 'same-origin' : 'omit', redirect: 'error', cache: 'no-store',
      headers: { Authorization: this.authorizationHeader(),
        Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      signal: requestSignal,
    });
    requestSignal.throwIfAborted();
    if (response.status === 401) { this.clearSession(); throw new Error(t('client_session_expired')); }
    if (!response.ok) throw new Error(response.status === 403 ? t('client_forbidden')
      : response.status === 507 ? t('client_storage_full')
      : response.status === 409 ? await marked(response, notConnectedType) ? t('client_connect_first') : t('client_unavailable_or_expired')
      : response.status === 503 && await marked(response, applicationMissingType) ? t('client_application_missing')
      : t('client_request_failed', { status: response.status }));
    if (response.status === 204) return undefined as T;
    const result = await readJson(response) as T;
    requestSignal.throwIfAborted();
    return result;
  }

  async download(path: string, fallbackFilename: string, signal?: AbortSignal): Promise<void> {
    const fullPath = `/JellySin/Lastfm/${path}`;
    if (!fullPath.startsWith('/') || fullPath.startsWith('//') || /[\r\n\\]/.test(fullPath)) throw new Error('Invalid API path.');
    const requestSignal = AbortSignal.any([this.sessionRequests.signal, AbortSignal.timeout(90_000), ...(signal ? [signal] : [])]);
    const response = await fetch(this.root + fullPath, {
      method: 'GET', credentials: 'omit', redirect: 'error', cache: 'no-store',
      headers: { Authorization: this.authorizationHeader() },
      signal: requestSignal,
    });
    requestSignal.throwIfAborted();
    if (response.status === 401) { this.clearSession(); throw new Error(t('client_session_expired')); }
    if (!response.ok) throw new Error(t('client_download_failed', { status: response.status }));
    const blob = await response.blob();
    if (typeof window !== 'undefined' && typeof document !== 'undefined') {
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = fallbackFilename;
      document.body.appendChild(link);
      link.click();
      link.remove();
      URL.revokeObjectURL(url);
    }
  }
}
