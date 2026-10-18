import { Client, type Session } from './client.js';
import { bind, element, message, run } from './view.js';
import { t } from './i18n.js';

interface QuickAttempt { secret: string; code: string }

export function setupAuthentication(client: Client, refresh: () => Promise<void>): void {
  let pending: AbortController | undefined;
  const beginAttempt = (): AbortController => {
    pending?.abort();
    element('quick-instructions').hidden = true;
    pending = new AbortController();
    return pending;
  };
  client.addEventListener('sessioncleared', () => pending?.abort());
  element<HTMLFormElement>('login-form').addEventListener('submit', event => {
    event.preventDefault();
    void run(element<HTMLButtonElement>('login-submit'), async () => {
      const current = beginAttempt();
      const password = element<HTMLInputElement>('password');
      const body = { Username: element<HTMLInputElement>('username').value, Pw: password.value };
      password.value = '';
      try {
        const session = await client.request<Session>('/Users/AuthenticateByName', 'POST', body, current.signal);
        current.signal.throwIfAborted();
        client.acceptSession(session);
        await refresh();
      } catch (error) { if (!current.signal.aborted) throw error; }
      finally { body.Pw = ''; if (pending === current) pending = undefined; }
    });
  });
  bind('quick-connect', async () => {
    const current = beginAttempt();
    if (!await client.request<boolean>('/QuickConnect/Enabled', 'GET', undefined, current.signal)) throw new Error(t('auth_quick_connect_disabled'));
    const attempt = await client.request<QuickAttempt>('/QuickConnect/Initiate', 'POST', undefined, current.signal);
    if (!/^[A-Za-z0-9]{4,12}$/.test(attempt.code) || typeof attempt.secret !== 'string') throw new Error(t('auth_quick_connect_invalid'));
    element('quick-code').textContent = attempt.code;
    element('quick-instructions').hidden = false;
    try {
      client.acceptSession(await awaitApproval(client, attempt.secret, current.signal));
      await refresh();
    } catch (error) { if (!current.signal.aborted) throw error; }
    finally {
      attempt.secret = '';
      if (pending === current) { pending = undefined; element('quick-instructions').hidden = true; }
    }
  });
  bind('quick-cancel', async () => { pending?.abort(); message(t('auth_quick_connect_cancelled')); });
  bind('logout', async () => {
    pending?.abort();
    void client.logout().catch((error: unknown) => {
      if (!client.signedIn) message(error instanceof Error ? error.message : t('auth_revocation_unconfirmed'), true);
    });
    await refresh();
    message(t('auth_signed_out_notice'));
  });
  window.addEventListener('pagehide', () => pending?.abort());
}

/** Polls until the code is approved; the bound stops an abandoned attempt. */
async function awaitApproval(client: Client, secret: string, signal: AbortSignal): Promise<Session> {
  for (let poll = 0; poll < 200; poll++) {
    await delay(signal);
    if (document.hidden) continue;
    const status = await client.api<{ authenticated: boolean }>('Auth/QuickConnect/Status', 'POST', { secret }, signal);
    if (status.authenticated) return client.request<Session>('/Users/AuthenticateWithQuickConnect', 'POST', { Secret: secret }, signal);
  }
  throw new Error(t('auth_quick_connect_expired'));
}

function delay(signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) { reject(new Error(t('auth_quick_connect_cancelled'))); return; }
    const cancel = (): void => { clearTimeout(timer); reject(new Error(t('auth_quick_connect_cancelled'))); };
    const timer = setTimeout(() => { signal.removeEventListener('abort', cancel); resolve(); }, 3000);
    signal.addEventListener('abort', cancel, { once: true });
  });
}
