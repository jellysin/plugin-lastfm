import { Client } from './client.js';
import { bind, element, link, message, run } from './view.js';

interface Connection { connected: boolean; username?: string; needsReconnect: boolean; scrobblingEnabled: boolean }
interface Me { userName: string; isAdministrator: boolean; connection: Connection }

export async function refreshAccount(client: Client): Promise<void> {
  element('sign-in').hidden = client.signedIn;
  element('signed-in').hidden = !client.signedIn;
  if (!client.signedIn) { element('account-label').textContent = 'Connect your music.'; return; }
  const me = await client.api<Me>('Me');
  element('account-label').textContent = me.userName;
  element('connection-status').textContent = me.connection.connected
    ? `Connected as ${me.connection.username ?? ''}${me.connection.needsReconnect ? ' · reconnect required' : ''}`
    : 'Connect Last.fm to start.';
  element<HTMLInputElement>('scrobbling').checked = me.connection.scrobblingEnabled;
  element<HTMLInputElement>('scrobbling').disabled = !me.connection.connected;
  element('administrator').hidden = !me.isAdministrator;
  const profile = element('profile-link');
  profile.replaceChildren();
  if (me.connection.username) link(profile, 'Your Last.fm profile ↗', `https://www.last.fm/user/${encodeURIComponent(me.connection.username)}`, true);
  if (me.isAdministrator) {
    const status = await client.api<{ configured: boolean }>('Admin/Application');
    element('application-status').textContent = status.configured ? 'Application credentials are configured.' : 'Application credentials are required before connecting.';
  }
}

export function setupAccount(client: Client, refresh: () => Promise<void>): void {
  let attemptId = '';
  client.addEventListener('sessioncleared', () => { attemptId = ''; });
  bind('connect-lastfm', async () => {
    const attempt = await client.api<{ attemptId: string; authorizationUrl: string }>('Me/Connection/Begin', 'POST');
    const target = new URL(attempt.authorizationUrl);
    if (target.protocol !== 'https:' || target.hostname !== 'www.last.fm' || target.pathname !== '/api/auth/') throw new Error('Invalid Last.fm authorization address.');
    attemptId = attempt.attemptId;
    const anchor = element<HTMLAnchorElement>('authorize-lastfm');
    anchor.href = target.href;
    element('finish-connection').hidden = false;
    anchor.focus();
    message('Open Last.fm, approve JellySin, then finish connecting here.');
  });
  bind('finish-lastfm', async () => {
    await client.api('Me/Connection/Finish', 'POST', { attemptId });
    client.resetAccount();
    attemptId = '';
    element('finish-connection').hidden = true;
    element<HTMLAnchorElement>('authorize-lastfm').removeAttribute('href');
    await refresh();
    message('Last.fm connected.');
  });
  bind('disconnect-lastfm', async () => {
    element('disconnect-confirmation').hidden = false;
    element('confirm-disconnect').focus();
  });
  bind('cancel-disconnect', async () => { element('disconnect-confirmation').hidden = true; });
  bind('confirm-disconnect', async () => {
    await client.api('Me/Connection', 'DELETE');
    client.resetAccount();
    element('disconnect-confirmation').hidden = true;
    await refresh();
    message('Last.fm disconnected and private plugin data cleared.');
  });
  element<HTMLInputElement>('scrobbling').addEventListener('change', event => {
    const enabled = (event.currentTarget as HTMLInputElement).checked;
    void run(null, async () => { await client.api('Me/Scrobbling', 'PUT', { enabled }); message(enabled ? 'Scrobbling enabled.' : 'Scrobbling paused.'); });
  });
  element<HTMLFormElement>('application-form').addEventListener('submit', event => {
    event.preventDefault();
    void run(element<HTMLButtonElement>('application-save'), async () => {
      const key = element<HTMLInputElement>('api-key');
      const secret = element<HTMLInputElement>('api-secret');
      const input = { apiKey: key.value, secret: secret.value };
      key.value = ''; secret.value = '';
      try { await client.api('Admin/Application', 'PUT', input); }
      finally { input.apiKey = ''; input.secret = ''; }
      await refresh();
      message('Application credentials saved. Existing accounts may need reconnection after changing the application.');
    });
  });
}
