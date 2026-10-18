import { Client } from './client.js';
import { bind, element, link, message, run } from './view.js';
import { refreshDevices, refreshMediaTypes, refreshScrobbleLog } from './rules.js';

interface Connection { connected: boolean; username?: string; needsReconnect: boolean; scrobblingEnabled: boolean; approvalPending?: boolean }
interface Me { userName: string; isAdministrator: boolean; connection: Connection }
interface LibraryItem { id: string; name: string; isAdminAllowed: boolean; isUserExcluded: boolean; isScrobbled: boolean }
interface UserLibrariesResponse { mode: string; libraries: LibraryItem[] }

const unconfirmed = 'Last.fm did not confirm the connection. Start again.';
let administrator = false;

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

const notStarted = 'Last.fm sign-in could not start.';

/** A refusal the page decides on its own, after the server answered. Its kind is told to the server, which otherwise sees nothing of it. */
export class StartRefusal extends Error {
  constructor(message: string, readonly reason: 'approval-address' | 'return-address') { super(message); }
}

/** What a refused return address tells the reader: an administrator can correct the server, anyone else can only pass it on. */
export function returnRefusal(named: string, expected: string, administrator: boolean): string {
  return administrator
    ? `${notStarted} The server names ${named} as the return address, but this page expects ${expected}. Set the published server address (start option published-server-url) to the address users open, or add the proxy under Networking, Known proxies. Then restart Jellyfin.`
    : `${notStarted} Ask the administrator of this server to check the server address setting.`;
}

/** The only address the page leaves for: the Last.fm approval page, asked to return to this server's own callback. */
export function approvalAddress(value: string, root: string, administrator = false): string {
  let target: URL;
  try { target = new URL(value); } catch { throw new StartRefusal(notStarted, 'approval-address'); }
  if (target.origin !== 'https://www.last.fm' || target.pathname !== '/api/auth/') throw new StartRefusal(notStarted, 'approval-address');
  // A server that does not know its public address names a return elsewhere; the token from Last.fm must not go there.
  const back = new URL(target.searchParams.get('cb') ?? '', location.origin);
  const path = `${root}/JellySin/Lastfm/Callback`;
  if (back.origin !== location.origin || back.pathname !== path) throw new StartRefusal(returnRefusal(back.origin + back.pathname, location.origin + path, administrator), 'return-address');
  return target.href;
}

function show(client: Client, me: Me): void {
  administrator = me.isAdministrator;
  element('account-label').textContent = me.userName;
  element('connect-lastfm').textContent = me.connection.connected ? 'Reconnect Last.fm' : 'Connect Last.fm';
  element('connection-status').textContent = me.connection.connected
    ? `Connected as ${me.connection.username ?? ''}${me.connection.needsReconnect ? ' · reconnect required' : ''}`
    : 'Connect Last.fm to start.';
  for (const el of document.querySelectorAll<HTMLElement>('[data-needs-connection]')) el.hidden = !me.connection.connected;
  element<HTMLInputElement>('scrobbling').checked = me.connection.scrobblingEnabled;
  element<HTMLInputElement>('scrobbling').disabled = !me.connection.connected;
  element('administrator').hidden = !me.isAdministrator;
  const profile = element('profile-link');
  profile.replaceChildren();
  if (me.connection.username) link(profile, 'Your Last.fm profile ↗', `https://www.last.fm/user/${encodeURIComponent(me.connection.username)}`, true);
  if (me.isAdministrator) element<HTMLAnchorElement>('server-settings-link').href = `${client.root}/web/index.html#/configurationpage?name=jellysin-lastfm`;
  if (me.connection.connected) {
    void refreshLibraries(client);
    void refreshDevices(client);
    void refreshMediaTypes(client);
    void refreshScrobbleLog(client);
  }
}

export function setupAccount(client: Client, refresh: () => Promise<void>): void {
  bind('connect-lastfm', async () => {
    const attempt = await client.api<{ authorizationUrl: string }>('Me/Connection/Begin', 'POST', { returnToSettings: false }, undefined, true);
    let target: string;
    try { target = approvalAddress(attempt.authorizationUrl, client.root, administrator); }
    catch (error) {
      // The server log gets one line for the click; the page shows its own text whether or not that line was written.
      if (error instanceof StartRefusal) await client.api('Me/Connection/Refusal', 'POST', { reason: error.reason }).catch(() => undefined);
      throw error;
    }
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

export async function refreshLibraries(client: Client): Promise<void> {
  const container = document.getElementById('library-exclusions');
  if (!container || container.hidden) return;
  try {
    const data = await client.api<UserLibrariesResponse>('Me/Libraries');
    renderLibraries(client, data);
  } catch {
    const summary = document.getElementById('library-exclusions-summary');
    if (summary) summary.textContent = 'Could not load library settings.';
  }
}

function renderLibraries(client: Client, data: UserLibrariesResponse): void {
  const summary = element('library-exclusions-summary');
  const list = element('library-exclusions-list');
  list.replaceChildren();
  if (!data.libraries || data.libraries.length === 0) {
    summary.textContent = 'No music libraries found on this server.';
    return;
  }
  summary.textContent = data.mode === 'AllowList'
    ? 'The server allows scrobbling only from selected libraries. You may exclude additional libraries.'
    : data.mode === 'BlockList'
    ? 'The server blocks scrobbling from certain libraries. You may exclude additional libraries.'
    : 'Select which libraries scrobble to your Last.fm account:';

  for (const lib of data.libraries) {
    const label = document.createElement('label');
    label.className = 'check';
    const input = document.createElement('input');
    input.type = 'checkbox';
    input.checked = lib.isScrobbled;
    input.disabled = !lib.isAdminAllowed;
    input.dataset.libraryId = lib.id;
    input.addEventListener('change', () => void onLibraryToggle(client, list));
    label.appendChild(input);
    const span = document.createElement('span');
    span.textContent = lib.isAdminAllowed ? ` ${lib.name}` : ` ${lib.name} (excluded by administrator)`;
    label.appendChild(span);
    list.appendChild(label);
  }
}

async function onLibraryToggle(client: Client, list: HTMLElement): Promise<void> {
  const inputs = list.querySelectorAll<HTMLInputElement>('input[type="checkbox"]');
  const excluded: string[] = [];
  for (const input of inputs) {
    if (!input.checked && input.dataset.libraryId) {
      excluded.push(input.dataset.libraryId);
    }
  }
  void run(null, async () => {
    await client.api('Me/Libraries', 'PUT', { excludedLibraries: excluded });
    message('Library scrobbling rules updated.');
  });
}

