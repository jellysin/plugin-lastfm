import type { Session } from './client.js';

interface ParentApiClient {
  accessToken?: () => unknown;
  serverId?: () => unknown;
  getCurrentUserId?: () => unknown;
}

type ParentProbe = 'absent' | 'pending' | Session;

function probeParentSession(serverId: string): ParentProbe {
  if (typeof window === 'undefined') {
    return 'absent';
  }
  try {
    if (window.parent === window) {
      return 'absent';
    }
    const parentApi = (window.parent as { ApiClient?: ParentApiClient }).ApiClient;
    if (!parentApi || typeof parentApi.accessToken !== 'function') {
      return 'pending';
    }
    const token = parentApi.accessToken();
    if (typeof token !== 'string' || !/^[a-f0-9]{32,128}$/i.test(token)) {
      return 'pending';
    }
    if (typeof parentApi.serverId === 'function') {
      const parentServerId = parentApi.serverId();
      if (typeof parentServerId === 'string' && parentServerId !== '' && parentServerId !== serverId) {
        // Different server id is final; undefined or empty id means ApiClient is still connecting.
        return 'absent';
      }
      if (typeof parentServerId !== 'string' || parentServerId !== serverId) {
        return 'pending';
      }
    }
    return {
      accessToken: token,
      serverId,
      user: { name: '' },
    };
  } catch {
    // Cross-origin parent frame access throws SecurityError; treat as unauthenticated.
    return 'absent';
  }
}

/** Reads the current session from the parent window if running inside an iframe on the same origin. */
export function parentSession(serverId: string): Session | undefined {
  const probe = probeParentSession(serverId);
  return typeof probe === 'object' ? probe : undefined;
}

/** Waits for a session from the parent window if running inside a same-origin iframe. */
export async function waitForParentSession(
  serverId: string,
  attempts = 50,
  intervalMs = 100,
): Promise<Session | undefined> {
  for (let attempt = 0; attempt < attempts; attempt++) {
    const probe = probeParentSession(serverId);
    if (probe === 'absent') {
      return undefined;
    }
    if (probe !== 'pending') {
      return probe;
    }
    if (attempt < attempts - 1 && intervalMs > 0) {
      await new Promise(resolve => setTimeout(resolve, intervalMs));
    }
  }
  return undefined;
}
