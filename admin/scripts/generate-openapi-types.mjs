/**
 * Regenerate `admin/lib/generated` from the factory's own OpenAPI document.
 *
 * The document is the API's account of what it returns — the Board's view records, named
 * by the `.Produces<T>()` annotations in `FactoryApp` — and `openapi-typescript` turns it
 * into the TypeScript the Board's hand-written view model is aliased over. There is no
 * served document and no route to one: generation is a build step, this script is the only
 * thing that runs it, and the two files it writes are committed so the contract is
 * reviewable in a diff.
 *
 * **The store of record is protected here, in two halves.** The generator produces the
 * document by launching the application's entry point, and this application's entry point
 * opens the SQLite store and reconciles orphaned rounds against it. `AgentFactory.Web.csproj`
 * sets `OpenApiGenerateDocumentsOnBuild=false`, so a `dotnet build` cannot reach `data/` at
 * all; and this script points `Factory__DatabasePath` at a scratch directory before it
 * invokes the target, so the one thing that does launch the entry point does so against a
 * throwaway database. `data/agent-factory.db` is not read, created or moved by either.
 *
 * **The generator carries its own compiler, and it has to.** `openapi-typescript` prints
 * through TypeScript's own AST API and declares a peer of `typescript: ^5.x`; this app's
 * TypeScript is 7, which no longer exposes that API. Installing the generator as a
 * dependency hands it the app's compiler through pnpm's workspace-root peer resolution and
 * it fails on the first import, and no `overrides` or `packageExtensions` entry changes that
 * (the peer stays rooted at the workspace). So it is run through `pnpm dlx`, which gives it
 * an isolated install carrying both halves, each pinned exactly — the app's own compiler and
 * dependency tree are untouched, and nothing is added to shadow them.
 */

import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

/** The generator, and the TypeScript it prints with. Both exact; neither is a dependency. */
const GENERATOR = 'openapi-typescript@7.13.0';
const COMPILER = 'typescript@5.9.3';

const scripts = import.meta.dirname;
const admin = resolve(scripts, '..');
const root = resolve(admin, '..');
const project = join(root, 'src', 'AgentFactory.Web', 'AgentFactory.Web.csproj');
const documentName = 'AgentFactory.Web.json';
const output = join(admin, 'lib', 'generated');

/** Run a command with its output shown, and fail loudly rather than carrying on. */
function run(command, args, environment) {
  // One command line rather than an args array: a shell is needed to run `pnpm` on
  // Windows, and Node warns when a shell is handed an unescaped args array. Every
  // argument here is a path or a flag, so quoting the ones with spaces is the whole of it.
  const line = [command, ...args].map((part) => (/\s/.test(part) ? `"${part}"` : part)).join(' ');

  const result = spawnSync(line, {
    stdio: 'inherit',
    env: environment,
    shell: true,
  });

  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(' ')} exited with ${result.status}`);
  }
}

const scratch = mkdtempSync(join(tmpdir(), 'agent-factory-openapi-'));

try {
  const environment = {
    ...process.env,
    Factory__Root: root,
    Factory__DatabasePath: join(scratch, 'agent-factory.db'),
  };

  // Generation is tied to neither `Build` nor a clock, so the target is invoked on its own —
  // and the cache beside the assembly is what says whether it ran last time. A regeneration
  // has to run whether or not the assembly moved, or the drift check would compare the
  // committed file with itself.
  rmSync(join(root, 'src', 'AgentFactory.Web', 'obj', 'AgentFactory.Web.OpenApiFiles.cache'), {
    force: true,
  });

  // The assembly first, so the target has something to read; then the generation itself.
  run('dotnet', ['build', project, '-v', 'quiet', '--nologo'], environment);
  run('dotnet', ['build', project, '-v', 'quiet', '--nologo', '-t:GenerateOpenApiDocuments'], environment);

  mkdirSync(output, { recursive: true });

  const generated = join(root, 'src', 'AgentFactory.Web', 'obj', documentName);
  copyFileSync(generated, join(output, 'openapi.json'));

  run('pnpm', [
    'dlx',
    `--package=${GENERATOR}`,
    `--package=${COMPILER}`,
    'openapi-typescript',
    join(output, 'openapi.json'),
    '-o',
    join(output, 'openapi.d.ts'),
  ], environment);
} finally {
  rmSync(scratch, { recursive: true, force: true });
}
