import assert from 'node:assert/strict';
import test from 'node:test';
import { Client, normalize, readJson } from '../../src/JellySin.Plugin.Lastfm/Web/client.js';

const bootstrap = { basePath: '/jellyfin', serverId: 'test-server', version: '1.0.0' };
const session = { accessToken: 'a'.repeat(32), serverId: 'test-server', user: { name: 'listener' } };

test('serializer profiles normalize without prototype pollution', () => {
  const value = normalize(JSON.parse('{"Tracks":[{"Artist":"Example","Title":"<script>bad()</script>"}],"__proto__":{"polluted":true}}'));
  assert.equal(value.tracks[0].artist, 'Example');
  assert.equal(value.tracks[0].title, '<script>bad()</script>');
  assert.equal(Object.getPrototypeOf(value), null);
  assert.equal({}.polluted, undefined);
});

test('response normalization rejects excessive depth and item counts', () => {
  let value = 'leaf';
  for (let index = 0; index < 18; index++) value = { child: value };
  assert.throws(() => normalize(value), /deeply nested/);
  assert.throws(() => normalize(Array(20_001).fill(null)), /too large/);
});

test('tokens stay in authorization headers and prefixed server paths', async context => {
  const client = new Client(bootstrap);
  client.acceptSession(session);
  context.mock.method(globalThis, 'fetch', async (url, options) => {
    assert.equal(url, '/jellyfin/JellySin/Lastfm/Me');
    assert.equal(String(url).includes(session.accessToken), false);
    assert.equal(options.credentials, 'omit');
    assert.equal(options.redirect, 'error');
    assert.match(options.headers.Authorization, /^MediaBrowser /);
    assert.ok(options.headers.Authorization.includes(`Token="${session.accessToken}"`));
    return Response.json({ UserName: 'listener' });
  });
  assert.equal((await client.api('Me')).userName, 'listener');
});

test('Quick Connect secret is sent in a body, never a URL', async context => {
  const client = new Client(bootstrap);
  context.mock.method(globalThis, 'fetch', async (url, options) => {
    assert.equal(url, '/jellyfin/JellySin/Lastfm/Auth/QuickConnect/Status');
    assert.equal(options.method, 'POST');
    assert.deepEqual(JSON.parse(options.body), { secret: 'test-secret' });
    return Response.json({ Authenticated: false });
  });
  assert.equal((await client.api('Auth/QuickConnect/Status', 'POST', { secret: 'test-secret' })).authenticated, false);
});

test('expired sessions clear persisted state and do not render response secrets', async context => {
  const state = new Map();
  const storage = { getItem: key => state.get(key), setItem: (key, value) => state.set(key, value), removeItem: key => state.delete(key) };
  const client = new Client(bootstrap, storage);
  client.acceptSession(session);
  context.mock.method(globalThis, 'fetch', async () => new Response('private server details', { status: 401 }));
  await assert.rejects(client.api('Me'), /session expired/);
  assert.equal(client.signedIn, false);
  assert.equal(state.size, 0);
});

test('storage failures allow memory-only login, and cross-server tokens are rejected', () => {
  const storage = { getItem() { throw new Error('disabled'); }, setItem() { throw new Error('disabled'); }, removeItem() { throw new Error('disabled'); } };
  const client = new Client(bootstrap, storage);
  client.acceptSession(session);
  assert.equal(client.signedIn, true);
  assert.throws(() => client.acceptSession({ ...session, serverId: 'another-server' }), /invalid sign-in session/);
  client.clearSession();
  assert.equal(client.signedIn, false);
});

test('invalid base paths and off-origin request paths are rejected', async () => {
  assert.throws(() => new Client({ ...bootstrap, basePath: '//evil.example' }), /invalid/);
  assert.throws(() => new Client({ ...bootstrap, basePath: '/test?token=abc' }), /invalid/);
  const client = new Client(bootstrap);
  await assert.rejects(client.request('//evil.example'), /Invalid API path/);
  await assert.rejects(client.request('/bad\\path'), /Invalid API path/);
});

test('HTTP errors expose safe status text rather than private response bodies', async context => {
  context.mock.method(globalThis, 'fetch', async () => new Response('secret-token-and-stack-trace', { status: 500 }));
  await assert.rejects(new Client(bootstrap).api('Me'), error => {
    assert.match(error.message, /HTTP 500/);
    assert.equal(error.message.includes('secret'), false);
    return true;
  });
});

test('chunked responses are cancelled as soon as the byte budget is exceeded', async () => {
  let cancelled = false;
  let reads = 0;
  const stream = new ReadableStream({
    pull(controller) { reads++; controller.enqueue(new Uint8Array(10)); },
    cancel() { cancelled = true; },
  });
  await assert.rejects(readJson(new Response(stream), 15), /too large/);
  assert.equal(cancelled, true);
  assert.ok(reads <= 3);
});

test('storage exhaustion explains the limit without exposing server response bodies', async context => {
  context.mock.method(globalThis, 'fetch', async () => new Response('secret-token-and-storage-path', { status: 507 }));
  await assert.rejects(new Client(bootstrap).api('Me/History/Preview'), error => {
    assert.match(error.message, /data storage limit/);
    assert.match(error.message, /saved progress is retained/);
    assert.equal(error.message.includes('secret'), false);
    return true;
  });
});

test('split UTF-8 responses decode without corrupting music titles', async () => {
  const bytes = new TextEncoder().encode('{"Title":"Björk"}');
  let index = 0;
  const stream = new ReadableStream({
    pull(controller) { if (index === bytes.length) controller.close(); else controller.enqueue(bytes.slice(index, ++index)); },
  });
  assert.equal((await readJson(new Response(stream))).title, 'Björk');
});

test('logging out cancels in-flight data so it cannot appear for the next account', async context => {
  const client = new Client(bootstrap);
  client.acceptSession(session);
  let complete;
  context.mock.method(globalThis, 'fetch', () => new Promise(resolve => { complete = resolve; }));
  const request = client.api('Me/History');
  client.clearSession();
  client.acceptSession({ ...session, accessToken: 'b'.repeat(32) });
  complete(Response.json({ Tracks: [{ Artist: 'private previous account' }] }));
  await assert.rejects(request, error => error.name === 'AbortError');
});

test('device identity also works on HTTP LAN origins without randomUUID', context => {
  context.mock.method(globalThis.crypto, 'randomUUID', () => { throw new Error('Unavailable outside secure contexts'); });
  const client = new Client(bootstrap);
  client.acceptSession(session);
  assert.equal(client.signedIn, true);
});

test('changing Last.fm account cancels data requests while retaining Jellyfin login', async context => {
  const client = new Client(bootstrap);
  client.acceptSession(session);
  let complete;
  context.mock.method(globalThis, 'fetch', () => new Promise(resolve => { complete = resolve; }));
  const request = client.api('Me/History');
  client.resetAccount();
  complete(Response.json({ Tracks: ['old account data'] }));
  await assert.rejects(request, error => error.name === 'AbortError');
  assert.equal(client.signedIn, true);
});

test('logout immediately clears private state and revokes only the previous server session', async context => {
  const state = new Map();
  const storage = { getItem: key => state.get(key), setItem: (key, value) => state.set(key, value), removeItem: key => state.delete(key) };
  const client = new Client(bootstrap, storage);
  client.acceptSession(session);
  let complete;
  context.mock.method(globalThis, 'fetch', (url, options) => {
    assert.equal(url, '/jellyfin/Sessions/Logout');
    assert.ok(options.headers.Authorization.includes(session.accessToken));
    assert.equal(options.signal.aborted, false);
    return new Promise(resolve => { complete = resolve; });
  });
  const revocation = client.logout();
  assert.equal(client.signedIn, false);
  assert.equal(state.size, 0);
  client.acceptSession({ ...session, accessToken: 'b'.repeat(32) });
  complete(new Response(null, { status: 401 }));
  await revocation;
  assert.equal(client.signedIn, true);
  assert.equal([...state.values()][0], 'b'.repeat(32));
});

test('a superseded request cannot return a session even when the transport completes late', async context => {
  const client = new Client(bootstrap);
  const attempt = new AbortController();
  let complete;
  context.mock.method(globalThis, 'fetch', () => new Promise(resolve => { complete = resolve; }));
  const request = client.request('/Users/AuthenticateByName', 'POST', {}, attempt.signal);
  attempt.abort();
  complete(Response.json(session));
  await assert.rejects(request, error => error.name === 'AbortError');
  assert.equal(client.signedIn, false);
});
