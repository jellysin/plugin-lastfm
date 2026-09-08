import { Client, type Session } from './client.js';
import { bind, element, message, run } from './view.js';

interface QuickAttempt { secret: string; code: string }

export function setupAuthentication(client: Client, refresh: () => Promise<void>): void {
  let pending: AbortController | undefined;
  client.addEventListener('sessioncleared', () => pending?.abort());
  element<HTMLFormElement>('login-form').addEventListener('submit', event => {
    event.preventDefault();
    void run(element<HTMLButtonElement>('login-submit'), async () => {
      pending?.abort();
      const password = element<HTMLInputElement>('password');
      const body = { Username: element<HTMLInputElement>('username').value, Pw: password.value };
      password.value = '';
      try { client.acceptSession(await client.request<Session>('/Users/AuthenticateByName', 'POST', body)); }
      finally { body.Pw = ''; }
      await refresh();
    });
  });
  bind('quick-connect', async () => {
    pending?.abort();
    const current = new AbortController();
    pending = current;
    if (!await client.request<boolean>('/QuickConnect/Enabled', 'GET', undefined, current.signal)) throw new Error('Quick Connect is disabled on this server.');
    const attempt = await client.request<QuickAttempt>('/QuickConnect/Initiate', 'POST', undefined, current.signal);
    if (!/^[A-Za-z0-9]{4,12}$/.test(attempt.code) || typeof attempt.secret !== 'string') throw new Error('Invalid Quick Connect response.');
    element('quick-code').textContent = attempt.code;
    element('quick-instructions').hidden = false;
    try {
      for (let poll = 0; poll < 200; poll++) {
        await delay(current.signal);
        if (document.hidden) continue;
        const status = await client.api<{ authenticated: boolean }>('Auth/QuickConnect/Status', 'POST', { secret: attempt.secret }, current.signal);
        if (status.authenticated) {
          client.acceptSession(await client.request<Session>('/Users/AuthenticateWithQuickConnect', 'POST', { Secret: attempt.secret }, current.signal));
          await refresh();
          return;
        }
      }
      throw new Error('Quick Connect expired. Start a new request.');
    } catch (error) { if (!current.signal.aborted) throw error; }
    finally {
      attempt.secret = '';
      if (pending === current) { pending = undefined; element('quick-instructions').hidden = true; }
    }
  });
  bind('quick-cancel', async () => { pending?.abort(); message('Quick Connect cancelled.'); });
  bind('logout', async () => {
    pending?.abort();
    try { await client.request('/Sessions/Logout', 'POST'); }
    finally { client.clearSession(); await refresh(); }
  });
  window.addEventListener('pagehide', () => pending?.abort());
}

function delay(signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) { reject(new Error('Quick Connect cancelled.')); return; }
    const cancel = (): void => { clearTimeout(timer); reject(new Error('Quick Connect cancelled.')); };
    const timer = setTimeout(() => { signal.removeEventListener('abort', cancel); resolve(); }, 3000);
    signal.addEventListener('abort', cancel, { once: true });
  });
}
