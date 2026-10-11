import { Client } from './client.js';
import { bind, element, link, message, run } from './view.js';

interface Connection { connected: boolean; username?: string; needsReconnect: boolean; scrobblingEnabled: boolean; approvalPending?: boolean }
interface Me { userName: string; isAdministrator: boolean; connection: Connection }

const unconfirmed = 'Last.fm did not confirm the connection. Start again.';

export async function refreshAccount(client: Client): Promise<void> {
  element('sign-in').hidden = client.signedIn;
  element('signed-in').hidden = !client.signedIn;
  if (!client.signedIn) { element('account-label').textContent = 'Connect your music.'; return; }
  const me = await client.api<Me>('Me');
  if (!me.connection.approvalPending) { show(client, me); return; }
  // The browser came back from Last.fm with an approval: complete it without a further step.
  let confirmed = true;
  try { await client.api('Me/Connection/Finish', 'POST'); }
  catch (error) { if (!client.signedIn) throw error; confirmed = false; }
  client.resetAccount();
  show(client, await client.api<Me>('Me'));
  if (!confirmed) throw new Error(unconfirmed);
  message('Last.fm connected.');
}

/** Reads the outcome a refused return left in the address and removes it, so that a reload does not repeat the notice. */
export function takeRefusedReturn(): boolean {
  const address = new URL(location.href);
  if (address.searchParams.get('connection') !== 'failed') return false;
  address.searchParams.delete('connection');
  history.replaceState(history.state, '', address.pathname + address.search + address.hash);
  return true;
}

/** The only address the page leaves for: the Last.fm approval page, asked to return to this server's own callback. */
export function approvalAddress(value: string, root: string): string {
  const target = new URL(value);
  const back = new URL(target.searchParams.get('cb') ?? '', location.origin);
  if (target.origin !== 'https://www.last.fm' || target.pathname !== '/api/auth/'
    || back.origin !== location.origin || back.pathname !== `${root}/JellySin/Lastfm/Callback`) throw new Error('Last.fm sign-in could not start.');
  return target.href;
}

function show(client: Client, me: Me): void {
  element('account-label').textContent = me.userName;
  element('connect-lastfm').textContent = me.connection.connected ? 'Reconnect Last.fm' : 'Connect Last.fm';
  element('connection-status').textContent = me.connection.connected
    ? `Connected as ${me.connection.username ?? ''}${me.connection.needsReconnect ? ' · reconnect required' : ''}`
    : 'Connect Last.fm to start.';
  element<HTMLInputElement>('scrobbling').checked = me.connection.scrobblingEnabled;
  element<HTMLInputElement>('scrobbling').disabled = !me.connection.connected;
  element('administrator').hidden = !me.isAdministrator;
  const profile = element('profile-link');
  profile.replaceChildren();
  if (me.connection.username) link(profile, 'Your Last.fm profile ↗', `https://www.last.fm/user/${encodeURIComponent(me.connection.username)}`, true);
  if (me.isAdministrator) element<HTMLAnchorElement>('server-settings-link').href = `${client.root}/web/index.html#/configurationpage?name=jellysin-lastfm`;
}

export function setupAccount(client: Client, refresh: () => Promise<void>): void {
  bind('connect-lastfm', async () => {
    const attempt = await client.api<{ authorizationUrl: string }>('Me/Connection/Begin', 'POST', { returnToSettings: false }, undefined, true);
    const target = approvalAddress(attempt.authorizationUrl, client.root);
    message('Opening Last.fm…');
    location.assign(target);
  });
  bind('disconnect-lastfm', async () => {
    element('disconnect-confirmation').hidden = false;
    element('confirm-disconnect').focus();
  });
  bind('cancel-disconnect', async () => { element('disconnect-confirmation').hidden = true; element('disconnect-lastfm').focus(); });
  bind('confirm-disconnect', async () => {
    await client.api('Me/Connection', 'DELETE');
    client.resetAccount();
    element('disconnect-confirmation').hidden = true;
    await refresh();
    message('Last.fm disconnected and private plugin data cleared.');
  });
  element<HTMLInputElement>('scrobbling').addEventListener('change', event => {
    const control = event.currentTarget as HTMLInputElement;
    const enabled = control.checked;
    control.disabled = true;
    void run(null, async () => {
      try { await client.api('Me/Scrobbling', 'PUT', { enabled }); message(enabled ? 'Scrobbling enabled.' : 'Scrobbling paused.'); }
      catch (error) { control.checked = !enabled; throw error; }
      finally { control.disabled = false; }
    });
  });
}
