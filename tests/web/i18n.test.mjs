import assert from 'node:assert/strict';
import test from 'node:test';
import { readdir, readFile } from 'node:fs/promises';
import { resolveLocale, t, setLocale, getLocale } from '../../src/JellySin.Plugin.Lastfm/Web/i18n.js';

const catalogDir = new URL('../../web/i18n/', import.meta.url);
const webDir = new URL('../../web/', import.meta.url);
const htmlFiles = [
  new URL('../../src/JellySin.Plugin.Lastfm/Web/index.html', import.meta.url),
  new URL('../../src/JellySin.Plugin.Lastfm/Web/admin.html', import.meta.url)
];
const keyCall = /\bt\(\s*['"]([a-zA-Z0-9_]+)['"]/g;
const keyAttribute = /data-i18n=['"]([a-zA-Z0-9_]+)['"]/g;

async function readCatalogs() {
  const names = (await readdir(catalogDir)).filter(name => name.endsWith('.json')).sort();
  const catalogs = new Map();
  for (const name of names) catalogs.set(name.slice(0, -'.json'.length), JSON.parse(await readFile(new URL(name, catalogDir), 'utf8')));
  return catalogs;
}

async function usedKeys() {
  const sources = (await readdir(webDir)).filter(name => name.endsWith('.ts') && name !== 'catalogs.ts').map(name => new URL(name, webDir));
  const keys = new Set();
  for (const url of [...sources, ...htmlFiles]) {
    const content = await readFile(url, 'utf8');
    for (const match of content.matchAll(keyCall)) keys.add(match[1]);
    for (const match of content.matchAll(keyAttribute)) keys.add(match[1]);
  }
  return keys;
}

const placeholders = text => [...text.matchAll(/\{([a-zA-Z0-9_]+)\}/g)].map(match => match[1]).sort();

test('every catalog has exactly the keys of en', async () => {
  const catalogs = await readCatalogs();
  assert.ok(catalogs.has('en') && catalogs.has('de'));
  const enKeys = Object.keys(catalogs.get('en')).sort();
  for (const [locale, catalog] of catalogs) assert.deepEqual(Object.keys(catalog).sort(), enKeys, `Keys differ in ${locale}.json`);
});

test('placeholders are identical across locales', async () => {
  const catalogs = await readCatalogs();
  const en = catalogs.get('en');
  for (const [locale, catalog] of catalogs) {
    for (const key of Object.keys(en)) assert.deepEqual(placeholders(catalog[key] ?? ''), placeholders(en[key]), `Placeholder mismatch in ${locale}.json: ${key}`);
  }
});

test('every key used in web/*.ts and *.html exists in en', async () => {
  const en = (await readCatalogs()).get('en');
  for (const key of await usedKeys()) assert.ok(Object.hasOwn(en, key), `Key used in source but missing from en catalog: ${key}`);
});

test('every en key is used somewhere in code or templates', async () => {
  const used = await usedKeys();
  for (const key of Object.keys((await readCatalogs()).get('en'))) assert.ok(used.has(key), `Key present in en catalog but unused in code or html: ${key}`);
});

test('locale resolution handles tags, subtags, fallbacks, and host preferences', () => {
  assert.equal(resolveLocale({ languages: ['de-AT'] }), 'de');
  assert.equal(resolveLocale({ languages: ['de-DE', 'en-US'] }), 'de');
  assert.equal(resolveLocale({ languages: ['fr-FR'] }), 'en');
  assert.equal(resolveLocale({ languages: ['es', 'de'] }), 'de');
  assert.equal(resolveLocale({ languages: [] }), 'en');
  assert.equal(resolveLocale({ languages: [''] }), 'en');

  assert.equal(resolveLocale({ isAdmin: true, hostLang: 'de' }), 'de');
  assert.equal(resolveLocale({ isAdmin: true, hostLang: 'de-CH' }), 'de');
  assert.equal(resolveLocale({ isAdmin: true, hostLang: 'fr' }), 'en');
  assert.equal(resolveLocale({ isAdmin: true, hostLang: '', languages: [] }), 'en');
  assert.equal(resolveLocale({ isAdmin: true, hostLang: null, languages: [] }), 'en');
});

test('t helper substitutes parameters and falls back gracefully', () => {
  const initial = getLocale();
  try {
    setLocale('en');
    assert.equal(t('account_connected_as', { username: 'Alice' }), 'Connected as Alice');
    assert.equal(t('non_existent_key_123'), 'non_existent_key_123');

    setLocale('de');
    assert.equal(t('account_connected_as', { username: 'Bob' }), 'Verbunden als Bob');
  } finally {
    setLocale(initial);
  }
});
