import assert from 'node:assert/strict';
import test from 'node:test';
import { readdir, readFile } from 'node:fs/promises';

// installServer opens the page, and the embedded page signs in and loads the account at once. A fixture value set after
// installServer but before passwordLogin races those first requests: the run passes or fails by timing. Such values go
// in installServer's `initial` option, which applies them before the page loads.
const webTests = new URL('./', import.meta.url);

export function racyAssignments(source) {
  const findings = [];
  let opened = false;
  source.split('\n').forEach((line, index) => {
    if (/await installServer\(/.test(line)) opened = true;
    else if (/await passwordLogin\(|^\s*\}\);/.test(line)) opened = false;
    else if (opened && /^\s*state\.[A-Za-z]+\s*=/.test(line)) findings.push(index + 1);
  });
  return findings;
}

test('no spec changes fixture state between opening the page and signing in', async () => {
  const specs = (await readdir(webTests)).filter(name => name.endsWith('.spec.mjs')).sort();
  assert.ok(specs.length > 0, 'no spec files found');
  const findings = [];
  for (const name of specs) {
    for (const line of racyAssignments(await readFile(new URL(name, webTests), 'utf8'))) findings.push(`${name}:${line}`);
  }
  assert.deepEqual(findings, [], 'set these through installServer(page, { initial: { ... } })');
});

test('the check finds a planted late assignment and accepts the initial option', () => {
  const planted = 'const state = await installServer(page);\n  state.needsReconnect = true;\n  await passwordLogin(page);\n';
  assert.deepEqual(racyAssignments(planted), [2]);
  const fixed = 'const state = await installServer(page, { initial: { needsReconnect: true } });\n  await passwordLogin(page);\n  state.deviceFail = true;\n';
  assert.deepEqual(racyAssignments(fixed), []);
});
