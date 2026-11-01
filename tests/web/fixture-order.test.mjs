import assert from 'node:assert/strict';
import test from 'node:test';
import { readdir, readFile } from 'node:fs/promises';

// installServer opens the page, and the embedded page signs in and loads the account at once. A fixture value set after
// installServer but before passwordLogin races those first requests: the run passes or fails by timing. Such values go
// in installServer's `initial` option, which applies them before the page loads.
const webTests = new URL('./', import.meta.url);

// Writes are found by a plain character scan, not a regular expression: no backtracking, linear in the line length.
const ASSIGNMENTS = ['**=', '??=', '||=', '&&=', '+=', '-=', '*=', '/=', '%=', '&=', '|=', '^=', '++', '--'];
const MUTATORS = new Set(['push', 'pop', 'shift', 'unshift', 'splice', 'set', 'delete', 'clear', 'add', 'fill', 'sort']);
const isIdentifierChar = char => char !== undefined && /[\w$]/.test(char);
const indentOf = line => line.length - line.trimStart().length;

// End of the member chain that starts at `start` (`.name` and `[...]` segments) and the last `.name` in it.
function memberChain(line, start) {
  let end = start;
  let last = '';
  for (let guard = 0; guard < line.length && end < line.length; guard += 1) {
    if (line[end] === '.' && isIdentifierChar(line[end + 1])) {
      let next = end + 1;
      while (next < line.length && isIdentifierChar(line[next])) next += 1;
      last = line.slice(end + 1, next);
      end = next;
    } else if (line[end] === '[') {
      const close = line.indexOf(']', end);
      if (close < 0) break;
      last = '';
      end = close + 1;
    } else break;
  }
  return { end, last };
}

// True when the `state` at `at` is written: assigned (plain, nested, bracketed, compound), incremented, or mutated.
function writesAt(line, at) {
  const { end, last } = memberChain(line, at + 'state'.length);
  if (end === at + 'state'.length) return false;
  const rest = line.slice(end).trimStart();
  if (rest.startsWith('=') && !rest.startsWith('==') && !rest.startsWith('=>')) return true;
  if (ASSIGNMENTS.some(operator => rest.startsWith(operator))) return true;
  if (rest.startsWith('(') && MUTATORS.has(last)) return true;
  const before = line.slice(0, at).trimEnd();
  return before.endsWith('++') || before.endsWith('--');
}

// True when the line writes the fixture state anywhere, inline conditionals included.
export function writesState(line) {
  if (line.replaceAll(' ', '').includes('Object.assign(state')) return true;
  for (let at = line.indexOf('state'); at >= 0; at = line.indexOf('state', at + 1)) {
    const previous = line[at - 1];
    if (!isIdentifierChar(previous) && previous !== '.' && writesAt(line, at)) return true;
  }
  return false;
}

// Parenthesis balance of a line, from `from` on; only the installServer call's own arguments need it.
function parenBalance(line, from) {
  let depth = 0;
  for (const char of line.slice(from)) depth += char === '(' ? 1 : char === ')' ? -1 : 0;
  return depth;
}

export function racyAssignments(source) {
  const findings = [];
  // openIndent: indentation of the open installServer statement, or -1 outside one. callDepth: unclosed parentheses
  // of its call; its arguments (an `initial` callback) run before the page loads and are not checked. The region ends
  // at passwordLogin or where the enclosing block closes, never at a nested callback's `});`.
  let openIndent = -1;
  let callDepth = 0;
  source.split('\n').forEach((line, index) => {
    const call = line.indexOf('await installServer(');
    if (call >= 0) {
      openIndent = indentOf(line);
      callDepth = parenBalance(line, call);
    } else if (openIndent < 0) return;
    else if (callDepth > 0) callDepth += parenBalance(line, 0);
    else if (line.includes('await passwordLogin(')) openIndent = -1;
    else if (line.trim().startsWith('}') && indentOf(line) < openIndent) openIndent = -1;
    else if (writesState(line)) findings.push(index + 1);
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
    '  if (slow) state.latencyMs = 500;',
    '  ++state.scrobbleLogCount;',
    '  await passwordLogin(page);',
    '});',
  ].join('\n');
  assert.deepEqual(racyAssignments(planted), [6, 7, 8, 9, 10, 11, 12, 13, 14]);
});

test('the check skips the installServer call arguments and checks from the line after the call', () => {
  const source = [
    'test(\'x\', async ({ page }) => {',
    '  const state = await installServer(page, {',
    '    initial: state => {',
    '      state.needsReconnect = true;',
    '      return {};',
    '    },',
    '  });',
    '  state.deviceFail = true;',
    '  await passwordLogin(page);',
    '});',
  ].join('\n');
  assert.deepEqual(racyAssignments(source), [8]);
});

test('the write scan stays linear on a long hostile line', () => {
  const line = `state${'.a'.repeat(50000)}${'['.repeat(50000)} == 1`;
  const started = performance.now();
  assert.equal(writesState(line), false);
  assert.ok(performance.now() - started < 1000, 'scan took longer than a second');
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
