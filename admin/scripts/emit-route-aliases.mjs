/**
 * The `.html` spelling of every exported route.
 *
 * `next build` with `trailingSlash: true` emits one directory index per route
 * (`projects/index.html`), which is what lets the factory serve `/projects/`
 * through its default-file handling. The links gate reads that same export and
 * resolves an anchor's `/projects` against the emitted route names, and the name
 * it derives for a directory index is `/projects/index` — so an anchor to
 * `/projects` resolves against `/projects` or `/projects/` and finds neither.
 * This app shipped no cross-page anchor for its whole first life, which is why
 * the gap between the two spellings was never exercised until the navigation
 * landed.
 *
 * So the build also writes `projects.html` beside `projects/index.html`: the same
 * page, at the address the gate's model names, served by the factory's static
 * handler like any other file. Next itself ships exactly this shape for the
 * not-found route (`404.html` beside `404/index.html`), so an alias is a spelling
 * of an exported route rather than a second kind of page. Nothing is linked to
 * the `.html` addresses; a reader who arrives at one gets the page it names.
 *
 * A file the export already wrote is never overwritten: `404.html` is Next's own
 * and is not a copy of `404/index.html`. Directories beginning with `_` are
 * Next's internals and are not routes a reader is served.
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
