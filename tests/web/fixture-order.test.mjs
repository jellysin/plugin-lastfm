import assert from 'node:assert/strict';
import test from 'node:test';
import { readdir, readFile } from 'node:fs/promises';

// installServer opens the page, and the embedded page signs in and loads the account at once. A fixture value set after
// installServer but before passwordLogin races those first requests: the run passes or fails by timing. Such values go
// in installServer's `initial` option, which applies them before the page loads.
const webTests = new URL('./', import.meta.url);

// Any write to the fixture state: plain, nested or bracketed assignment, compound assignment, increment, a mutating
// method on a nested value, or Object.assign onto it.
const stateWrite = /^\s*(?:(?:\+\+|--)\s*state\b|state(?:\.[A-Za-z_$][\w$]*|\[[^\]]+\])+\s*(?:[-+*/%&|^]?=(?!=)|\+\+|--|\.(?:push|pop|shift|unshift|splice|set|delete|clear|add)\()|Object\.assign\(\s*state\b)/;
const indentOf = line => line.length - line.trimStart().length;

export function racyAssignments(source) {
  const findings = [];
  // Indentation of the open installServer call, or -1 outside one. The region ends at passwordLogin or where the
  // enclosing block closes, never at a nested callback's `});`.
  let openIndent = -1;
  source.split('\n').forEach((line, index) => {
    if (line.includes('await installServer(')) openIndent = indentOf(line);
    else if (openIndent < 0) return;
    else if (line.includes('await passwordLogin(')) openIndent = -1;
    else if (line.trim().startsWith('}') && indentOf(line) < openIndent) openIndent = -1;
    else if (stateWrite.test(line)) findings.push(index + 1);
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

test('the check finds planted late writes of every form', () => {
  const planted = [
    'test(\'x\', async ({ page }) => {',
    '  const state = await installServer(page);',
    '  await page.route(/x/, route => {',
    '    route.continue();',
    '  });',
    '  state.needsReconnect = true;',
    '  state.delivery.Pending = 2;',
    '  state[\'deviceFail\'] = true;',
    '  state.recipes.push({});',
    '  state.scrobbleLogCount += 1;',
    '  state.scrobbleLogCount++;',
    '  Object.assign(state, { connected: true });',
    '  await passwordLogin(page);',
    '});',
  ].join('\n');
  assert.deepEqual(racyAssignments(planted), [6, 7, 8, 9, 10, 11, 12]);
});

test('the check accepts the initial option, comparisons and writes after sign-in or after the test', () => {
  const fixed = [
    'test(\'x\', async ({ page }) => {',
    '  const state = await installServer(page, { initial: { needsReconnect: true } });',
    '  expect(state.connected === true).toBe(true);',
    '  await passwordLogin(page);',
    '  state.deviceFail = true;',
    '});',
    'test(\'y\', async ({ page }) => {',
    '  const state = await installServer(page);',
    '});',
    'state.unrelated = 1;',
  ].join('\n');
  assert.deepEqual(racyAssignments(fixed), []);
});
