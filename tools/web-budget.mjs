import { readdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

export const BUDGET = 150_000;

/** Sum the gzip sizes of the JavaScript modules in one directory URL; refuse none, or more than the budget. */
export async function measure(root, budget = BUDGET) {
  const files = (await readdir(root)).filter(name => name.endsWith('.js'));
  if (!files.length) throw new Error('JavaScript budget: no compiled modules found.');
  let total = 0;
  for (const name of files) total += gzipSync(await readFile(new URL(name, root))).length;
  if (total > budget) throw new Error(`JavaScript budget exceeded: ${total} bytes gzip.`);
  return { modules: files.length, total };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const { modules, total } = await measure(new URL('../src/JellySin.Plugin.Lastfm/Web/', import.meta.url));
  process.stdout.write(`JavaScript: ${modules} modules, ${total} bytes gzip (budget ${BUDGET}).\n`);
}
