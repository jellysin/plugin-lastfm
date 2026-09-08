import { readdir, readFile } from 'node:fs/promises';
import { gzipSync } from 'node:zlib';

const root = new URL('../src/JellySin.Plugin.Lastfm/Web/', import.meta.url);
const files = (await readdir(root)).filter(name => name.endsWith('.js'));
let total = 0;
for (const name of files) total += gzipSync(await readFile(new URL(name, root))).length;
if (!files.length || total > 150_000) throw new Error(`JavaScript budget exceeded: ${total} bytes gzip.`);
process.stdout.write(`JavaScript: ${files.length} modules, ${total} bytes gzip (budget 150000).\n`);
