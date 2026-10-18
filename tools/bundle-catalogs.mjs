import { readdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const i18nDir = new URL('../web/i18n/', import.meta.url);
const targetFile = new URL('../web/catalogs.ts', import.meta.url);

export async function bundleCatalogs() {
  const files = (await readdir(i18nDir)).filter(name => name.endsWith('.json')).sort();
  const catalogs = {};
  for (const name of files) {
    const locale = name.replace(/\.json$/, '');
    const content = JSON.parse(await readFile(new URL(name, i18nDir), 'utf8'));
    catalogs[locale] = content;
  }
  const code = `// Generated from web/i18n/*.json by tools/bundle-catalogs.mjs. Do not edit directly.
export const catalogs: Record<string, Record<string, string>> = ${JSON.stringify(catalogs, null, 2)};
`;
  await writeFile(targetFile, code, 'utf8');
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  await bundleCatalogs();
  process.stdout.write('Bundled i18n catalogs into web/catalogs.ts\n');
}
