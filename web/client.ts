export interface Bootstrap { basePath: string; serverId: string; version: string }
export interface Session { accessToken: string; user: { name: string }; serverId: string }

/** Stop reading as soon as the byte budget is exhausted, including chunked responses. */
export async function readJson(response: Response, limit = 4_000_000): Promise<unknown> {
  const reader = response.body?.getReader();
  if (!reader) throw new Error('The server returned an empty response.');
  const decoder = new TextDecoder('utf-8', { fatal: true });
  const parts: string[] = [];
  let bytes = 0;
  try {
    for (;;) {
      const chunk = await reader.read();
      if (chunk.done) break;
      bytes += chunk.value.byteLength;
      if (bytes > limit) throw new Error('The server response is too large.');
      parts.push(decoder.decode(chunk.value, { stream: true }));
    }
    parts.push(decoder.decode());
    return normalize(JSON.parse(parts.join('')) as unknown);
  } finally { await reader.cancel(); reader.releaseLock(); }
}

/** JSON from either Jellyfin serializer profile is normalized with a fixed nesting bound. */
export function normalize(value: unknown, depth = 0): unknown {
  if (depth > 16) throw new Error('The server response is too deeply nested.');
  if (Array.isArray(value)) {
    if (value.length > 20_000) throw new Error('The server response is too large.');
    return value.map((entry: unknown) => normalize(entry, depth + 1));
  }
  if (value !== null && typeof value === 'object') {
    const result: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
    for (const [key, entry] of Object.entries(value).slice(0, 200)) {
      if (['__proto__', 'constructor', 'prototype'].includes(key)) continue;
      result[key.charAt(0).toLowerCase() + key.slice(1)] = normalize(entry, depth + 1);
    }
    return result;
  }
  return value;
}

export class Client extends EventTarget {
  private token = '';
  private sessionRequests = new AbortController();
  private readonly deviceId = Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('');
  private readonly storageKey: string;
  readonly root: string;

  constructor(readonly config: Bootstrap, private readonly storage: Storage | undefined) {
    super();
    if (!/^\/(?:[^?#\\\r\n]*)$/.test(config.basePath || '/') || config.basePath.startsWith('//')) {
      throw new Error('The server base path is invalid.');
    }
    this.root = config.basePath.replace(/\/$/, '');
    this.storageKey = `jellysin:${config.serverId}:${this.root}`;
    try { this.token = storage?.getItem(this.storageKey) ?? ''; } catch { this.token = ''; }
    if (!/^[a-f0-9]{32,128}$/i.test(this.token)) this.token = '';
  }

  get signedIn(): boolean { return this.token.length > 0; }

  acceptSession(session: Session): void {
    if (!/^[a-f0-9]{32,128}$/i.test(session.accessToken) || session.serverId !== this.config.serverId) {
      throw new Error('The server returned an invalid sign-in session.');
    }
    if (this.token) this.clearSession();
    this.sessionRequests.abort();
    this.sessionRequests = new AbortController();
    this.token = session.accessToken;
    try { this.storage?.setItem(this.storageKey, this.token); } catch { /* Memory-only sign-in remains available. */ }
  }

  clearSession(): void {
    this.resetAccount();
    this.token = '';
    try { this.storage?.removeItem(this.storageKey); } catch { /* Storage may be disabled. */ }
    this.dispatchEvent(new Event('sessioncleared'));
  }

  resetAccount(): void {
    this.sessionRequests.abort();
    this.sessionRequests = new AbortController();
    this.dispatchEvent(new Event('accountchanged'));
  }

  async api<T>(path: string, method = 'GET', body?: unknown, signal?: AbortSignal): Promise<T> {
    return this.request<T>(`/JellySin/Lastfm/${path}`, method, body, signal);
  }

  async request<T>(path: string, method = 'GET', body?: unknown, signal?: AbortSignal): Promise<T> {
    if (!path.startsWith('/') || path.startsWith('//') || /[\r\n\\]/.test(path)) throw new Error('Invalid API path.');
    const header = `MediaBrowser Client="JellySin%20Last.fm", Device="Browser", DeviceId="${this.deviceId}", Version="${encodeURIComponent(this.config.version)}"`;
    const sessionSignal = this.sessionRequests.signal;
    const response = await fetch(this.root + path, {
      method, credentials: 'omit', redirect: 'error', cache: 'no-store',
      headers: { Authorization: header + (this.token ? `, Token="${this.token}"` : ''),
        Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      signal: AbortSignal.any([sessionSignal, AbortSignal.timeout(90_000), ...(signal ? [signal] : [])]),
    });
    sessionSignal.throwIfAborted();
    if (response.status === 401) { this.clearSession(); throw new Error('Your Jellyfin session expired. Sign in again.'); }
    if (!response.ok) throw new Error(response.status === 403 ? 'Your account cannot perform this action.'
      : response.status === 409 ? 'This action is unavailable or its preview expired. Refresh and try again.'
      : `The request could not be completed (HTTP ${response.status}).`);
    if (response.status === 204) return undefined as T;
    const result = await readJson(response) as T;
    sessionSignal.throwIfAborted();
    return result;
  }
}
