import type { Session } from './client.js';

interface ParentApiClient {
  accessToken?: () => unknown;
  serverId?: () => unknown;
  getCurrentUserId?: () => unknown;
}

/** Reads the current session from the parent window if running inside an iframe on the same origin. */
export function parentSession(serverId: string): Session | undefined {
  if (typeof window === 'undefined' || window.parent === window) {
    return undefined;
  }
  try {
    const parentApi = (window.parent as { ApiClient?: ParentApiClient }).ApiClient;
    if (!parentApi || typeof parentApi.accessToken !== 'function') {
      return undefined;
    }
    const token = parentApi.accessToken();
    if (typeof token !== 'string' || !/^[a-f0-9]{32,128}$/i.test(token)) {
      return undefined;
    }
    if (typeof parentApi.serverId === 'function') {
      const parentServerId = parentApi.serverId();
      if (typeof parentServerId !== 'string' || parentServerId !== serverId) {
        return undefined;
      }
    }
    return {
      accessToken: token,
      serverId,
      user: { name: '' },
    };
  } catch {
    // Cross-origin parent frame access throws SecurityError; treat as unauthenticated.
    return undefined;
  }
}
