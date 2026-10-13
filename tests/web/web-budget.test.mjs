import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { pathToFileURL } from 'node:url';
import { BUDGET, measure } from '../../tools/web-budget.mjs';

async function directory(t, files) {
  const path = await mkdtemp(join(tmpdir(), 'jellysin-web-budget-'));
  t.after(() => rm(path, { recursive: true, force: true }));
  for (const [name, content] of Object.entries(files)) await writeFile(join(path, name), content);
  return pathToFileURL(path + '/');
}

test('the budget counts only JavaScript modules and passes at its limit', async t => {
  const root = await directory(t, { 'a.js': 'export const a = 1;\n', 'b.js': 'export const b = 2;\n', 'notes.txt': randomBytes(BUDGET + 1) });
  const { modules, total } = await measure(root);
  assert.equal(modules, 2);
  assert.deepEqual(await measure(root, total), { modules: 2, total });
  await assert.rejects(measure(root, total - 1), /JavaScript budget exceeded/);
});

test('a planted incompressible module over the budget is refused', async t => {
  const root = await directory(t, { 'planted.js': randomBytes(BUDGET + 1024) });
  await assert.rejects(measure(root), /JavaScript budget exceeded/);
});

test('a directory without compiled modules is refused', async t => {
  await assert.rejects(measure(await directory(t, { 'readme.txt': 'none' })), /no compiled modules/);
});
