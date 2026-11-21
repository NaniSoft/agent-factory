/**
 * The `.html` spelling of every exported route.
 *
 * `next build` with `trailingSlash: true` emits one directory index per route
 * (`pipelines/index.html`). The factory serves `/pipelines/` through its
 * default-file handling; a cold load of `/pipelines` resolves through the alias
 * this script writes beside the directory. Next ships the same shape for the
 * not-found route, so an alias is a spelling of an exported route rather than a
 * second kind of page.
 */

import { copyFileSync, existsSync, readdirSync } from 'node:fs';
import path from 'node:path';

const outDir = path.resolve('out');

if (!existsSync(outDir)) {
  console.error('emit-route-aliases: out/ does not exist. Run `next build` first.');
  process.exit(1);
}

const written = [];
const skipped = [];

for (const entry of readdirSync(outDir, { withFileTypes: true })) {
  if (!entry.isDirectory() || entry.name.startsWith('_')) continue;

  const indexHtml = path.join(outDir, entry.name, 'index.html');
  if (!existsSync(indexHtml)) continue;

  const alias = path.join(outDir, `${entry.name}.html`);
  if (existsSync(alias)) {
    skipped.push(`${entry.name}.html (the export already wrote it)`);
    continue;
  }

  copyFileSync(indexHtml, alias);
  written.push(`${entry.name}.html`);
}

for (const file of written) {
  console.log(`emit-route-aliases: wrote ${file}`);
}
for (const file of skipped) {
  console.log(`emit-route-aliases: left alone ${file}`);
}
