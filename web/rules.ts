import { Client } from './client.js';
import { element, message, run } from './view.js';

export interface UserDeviceView {
  id: string;
  name: string;
  appName?: string;
  isExcluded: boolean;
}

export interface UserMediaTypeView {
  type: string;
  isAdminAllowed: boolean;
  isUserExcluded: boolean;
  isScrobbled: boolean;
}

export async function refreshDevices(client: Client): Promise<void> {
  const container = document.getElementById('device-exclusions');
  if (!container || container.hidden) return;
  try {
    const data = await client.api<UserDeviceView[]>('Me/Devices');
    renderDevices(client, data);
  } catch {
    const summary = document.getElementById('device-exclusions-summary');
    if (summary) summary.textContent = 'Could not load devices.';
  }
}

function renderDevices(client: Client, devices: UserDeviceView[]): void {
  const summary = element('device-exclusions-summary');
  const list = element('device-exclusions-list');
  list.replaceChildren();
  if (!devices || devices.length === 0) {
    summary.textContent = 'No devices found for your account.';
    return;
  }
  summary.textContent = 'Select which devices scrobble to your Last.fm account:';
  for (const device of devices) {
    const label = document.createElement('label');
    label.className = 'check';
    const input = document.createElement('input');
    input.type = 'checkbox';
    input.checked = !device.isExcluded;
    input.dataset.deviceId = device.id;
    input.addEventListener('change', () => void onDeviceToggle(client, list));
    label.appendChild(input);
    const span = document.createElement('span');
    const app = device.appName ? ` (${device.appName})` : '';
    span.textContent = ` ${device.name}${app}`;
    label.appendChild(span);
    list.appendChild(label);
  }
}

async function onDeviceToggle(client: Client, list: HTMLElement): Promise<void> {
  const inputs = list.querySelectorAll<HTMLInputElement>('input[type="checkbox"]');
  const excluded: string[] = [];
  for (const input of inputs) {
    if (!input.checked && input.dataset.deviceId) {
      excluded.push(input.dataset.deviceId);
    }
  }
  void run(null, async () => {
    await client.api('Me/Devices', 'PUT', { excludedDevices: excluded });
    message('Device scrobbling rules updated.');
  });
}

function mediaTypeName(type: string): string {
  switch (type) {
    case 'Music': return 'Music tracks';
    case 'Audiobooks': return 'Audiobook chapters';
    case 'MusicVideos': return 'Music videos';
    default: return type;
  }
}

export async function refreshMediaTypes(client: Client): Promise<void> {
  const container = document.getElementById('media-type-exclusions');
  if (!container || container.hidden) return;
  try {
    const data = await client.api<UserMediaTypeView[]>('Me/MediaTypes');
    renderMediaTypes(client, data);
  } catch {
    const summary = document.getElementById('media-type-exclusions-summary');
    if (summary) summary.textContent = 'Could not load media type settings.';
  }
}

function renderMediaTypes(client: Client, mediaTypes: UserMediaTypeView[]): void {
  const summary = element('media-type-exclusions-summary');
  const list = element('media-type-exclusions-list');
  list.replaceChildren();
  if (!mediaTypes || mediaTypes.length === 0) {
    summary.textContent = 'No media types available.';
    return;
  }
  summary.textContent = 'Select which media types scrobble to your Last.fm account:';
  for (const item of mediaTypes) {
    const label = document.createElement('label');
    label.className = 'check';
    const input = document.createElement('input');
    input.type = 'checkbox';
    input.checked = item.isScrobbled;
    input.disabled = !item.isAdminAllowed;
    input.dataset.mediaType = item.type;
    input.dataset.userExcluded = String(item.isUserExcluded);
    input.addEventListener('change', () => void onMediaTypeToggle(client, list));
    label.appendChild(input);
    const span = document.createElement('span');
    const name = mediaTypeName(item.type);
    span.textContent = item.isAdminAllowed ? ` ${name}` : ` ${name} (Turned off by the administrator)`;
    label.appendChild(span);
    list.appendChild(label);
  }
}

async function onMediaTypeToggle(client: Client, list: HTMLElement): Promise<void> {
  const inputs = list.querySelectorAll<HTMLInputElement>('input[type="checkbox"]');
  const excluded: string[] = [];
  for (const input of inputs) {
    // A type the administrator turned off keeps the user's own choice for when it is turned on again.
    const off = input.disabled ? input.dataset.userExcluded === 'true' : !input.checked;
    if (off && input.dataset.mediaType) excluded.push(input.dataset.mediaType);
  }
  void run(null, async () => {
    await client.api('Me/MediaTypes', 'PUT', { excludedMediaTypes: excluded });
    message('Media type scrobbling rules updated.');
  });
}

export interface ScrobbleLogSettings {
  enabled: boolean;
  count: number;
}

export async function refreshScrobbleLog(client: Client): Promise<void> {
  const container = document.getElementById('scrobble-log');
  if (!container || container.hidden) return;
  try {
    const data = await client.api<ScrobbleLogSettings>('Me/ScrobbleLog/Settings');
    renderScrobbleLog(data);
  } catch {
    const countEl = document.getElementById('scrobble-log-count');
    if (countEl) countEl.textContent = 'Could not load scrobble log settings.';
  }
}

function renderScrobbleLog(data: ScrobbleLogSettings): void {
  const checkbox = document.getElementById('scrobble-log-enabled') as HTMLInputElement | null;
  const countEl = document.getElementById('scrobble-log-count');
  if (checkbox) checkbox.checked = Boolean(data.enabled);
  if (countEl) countEl.textContent = `${data.count ?? 0} entries logged.`;
}

export function setupScrobbleLog(client: Client): void {
  const checkbox = document.getElementById('scrobble-log-enabled') as HTMLInputElement | null;
  if (checkbox) {
    checkbox.addEventListener('change', () => {
      void run(null, async () => {
        const res = await client.api<ScrobbleLogSettings>('Me/ScrobbleLog/Settings', 'PUT', { enabled: checkbox.checked });
        renderScrobbleLog(res);
        message(checkbox.checked ? 'Scrobble log enabled.' : 'Scrobble log disabled and cleared.');
      });
    });
  }

  const jsonBtn = document.getElementById('scrobble-log-download-json');
  if (jsonBtn) {
    jsonBtn.addEventListener('click', () => {
      void run(null, async () => {
        await client.download('Me/ScrobbleLog/Export?format=json', 'scrobbles.json');
      });
    });
  }

  const csvBtn = document.getElementById('scrobble-log-download-csv');
  if (csvBtn) {
    csvBtn.addEventListener('click', () => {
      void run(null, async () => {
        await client.download('Me/ScrobbleLog/Export?format=csv', 'scrobbles.csv');
      });
    });
  }
}
