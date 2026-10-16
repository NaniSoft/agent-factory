# Which OpenAPI-to-TypeScript generator, and does it survive the consumer gates

Research for [#57](https://github.com/NaniSoft/agent-factory/issues/57), feeding the
implementation ticket [#80](https://github.com/NaniSoft/agent-factory/issues/80).

Every claim below is either read out of a file in this repository, read out of the
installed `@nanisoft/prism-ui` gate kit, or observed by running the thing on this
machine. Where something could not be verified it says so in the last section rather
than guessing.

## The short answer

**`openapi-typescript`, types only, into `admin/lib/generated/`. A generated client
is not wanted here and should not be adopted.**

Three findings decide it, and all three were observed rather than inferred:

1. **The consumer gates do not object.** All seven gates stayed green with a full
   `openapi-typescript` output *and* a full Orval React Query client sitting inside
   `admin/lib/generated/`, with the real lockfile carrying both generators'
   dependency trees. The gate kit is not the obstacle. That is the opposite of what
   the ticket was worried about, and it is worth saying plainly because the worry was
   reasonable.
2. **But the document the .NET side emits today is empty where it matters.** Every
   one of the eight routes' `200` responses came out as `{"description": "OK"}` with
   no schema, so none of the seven view models the Board mirrors appear in
   `components.schemas` at all. Codegen against the document as it stands produces
   types that carry no information. This is a property of the endpoints, not of any
   generator, and it has to be fixed on the .NET side first.
3. **Build-time generation runs the app.** `dotnet build` invoking the entry point
   created `data/agent-factory.db` and ran `Orchestrator.RecoverOrphanedRounds()`
   against it — a build writing to the store of record. There is a clean
   mitigation and it is one environment variable, but it has to be stated.

The hand-written types are not wrong today. `admin/lib/decisions.ts`'s
`DecisionResult` and `src/agent-factory/Api/DecisionView.cs`'s `DecisionResult` agree
field for field, camel-cased name for camel-cased name. So the case for codegen is
not "the hand-written types have drifted"; it is that a generator removes the
possibility rather than the occurrence.

## How this was checked

Everything below is reproducible. In a throwaway worktree:

```bash
# 1. make the document exist (two packages and one call)
dotnet add src/agent-factory/agent-factory.csproj package Microsoft.AspNetCore.OpenApi
dotnet add src/agent-factory/agent-factory.csproj package Microsoft.Extensions.ApiDescription.Server
#    and builder.Services.AddOpenApi() in FactoryApp.Create
dotnet build src/agent-factory/agent-factory.csproj
#    -> src/agent-factory/obj/agent-factory.json

# 2. generate with all three
npx openapi-typescript openapi.json -o openapi.d.ts
npx orval --config orval.config.mjs      # react-query + fetch, baseUrl: ''
kiota generate -l TypeScript -d openapi.json -o kiota-ts -n agent-factory

# 3. drop the output into the Board and run the gates
pnpm add -D openapi-typescript orval && pnpm add @tanstack/react-query
mkdir -p admin/lib/generated && cp the-outputs admin/lib/generated/
npx next build && node scripts/emit-route-aliases.mjs
node node_modules/@nanisoft/prism-ui/gates/cli.mjs --json
```

Versions used, resolved on 2026-10-16: `openapi-typescript` 7.13.0, `orval` 8.43.0,
`kiota` 1.36.1, `Microsoft.AspNetCore.OpenApi` / `Microsoft.Extensions.ApiDescription.Server`
10.0.13, Next 16.4.0, Node 24.18.1, pnpm 11.18.0.

## 1. What the .NET side emits, and what it costs

### The three lines that make the document exist

`Microsoft.AspNetCore.OpenApi` registers the generator; `AddOpenApi()` turns it on;
`MapOpenApi()` would *serve* it, and should not be called here.
Microsoft's own overview is the source:
<https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/overview?view=aspnetcore-10.0>.
It is explicit that the endpoint "is restricted to the `Development` environment to
minimize the risk of exposing sensitive information" — and this process binds
loopback and serves a Board, so the right answer is to not map it at all and read
the build-time file.

`Microsoft.Extensions.ApiDescription.Server` is the second package; it is what makes
generation a *build* step rather than a runtime one.
<https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0>
gives both halves:

> Upon installation, this package:
> - Automatically generates the Open API documents associated with the app during build.
> - Populates the Open API documents in the app's output directory.

Observed: `dotnet build` on this project, unchanged except for the two
`PackageReference` lines and one `AddOpenApi()`, emitted
`src/agent-factory/obj/agent-factory.json` in about six seconds. The build log names
the step and the tool:

```
GenerateOpenApiDocuments:
  dotnet "...\microsoft.extensions.apidescription.server\10.0.13\build/../tools/dotnet-getdocument.dll"
    --assembly "...\bin\Debug\net10.0\agent-factory.dll" ... --output "...\obj" --project "agent-factory"
```

So: **yes, build-time only, no need to run the app by hand.** Output lands in `obj/`
by default; `<OpenApiDocumentsDirectory>` moves it
(<https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/overview?view=aspnetcore-10.0>).
It is OpenAPI **3.1.1** with JSON Schema draft 2020-12 by default in .NET 10 — a fact
that matters for generator choice and is checked in §2.

### The part that is not build-time only: it runs the entry point

Same Microsoft page, under "Customize runtime behavior during build-time document
generation":

> Build-time OpenAPI document generation functions by **launching the apps entrypoint
> with a mock server implementation**. A mock server is required to produce accurate
> OpenAPI documents because all information in the OpenAPI document can't be
> statically analyzed. Because the apps entrypoint is invoked, any logic in the apps
> startup is invoked. This includes code that injects services into the DI container
> or reads from configuration.

This is not a warning about other people's apps. `Program.cs` is four lines into
`FactoryApp.Create`, and `FactoryApp.Create` does three things at composition time
that touch the filesystem:

- `ProjectFileLoader.Load(options.FactoriesDirectory, ...)` (`FactoryApp.cs:132`)
- `new SqliteWorkItemStore(options.DatabasePath, ...)` (`FactoryApp.cs:127`)
- `app.Services.GetRequiredService<Orchestrator>().RecoverOrphanedRounds()`
  (`FactoryApp.cs:199`) — which **writes**: `ClearPendingRound`, and `Move` to
  `Frontier` for anything left `InProgress` (`Loop/Orchestrator.cs:291-306`)

Observed, with `data/` deleted immediately before:

```
$ dotnet build src/agent-factory/agent-factory.csproj
  info: agent-factory.projects[0]
        Serving 1 project(s) from ...\wt-codegen\factories, in rotation order, and refused 0 file(s).
  Writing document named 'v1' to '...\src\agent-factory\obj\agent-factory.json'.

$ ls data/
agent-factory.db   45056 bytes
```

A build created the factory's SQLite database and ran its orphan-round recovery
against it. Against a live `data/agent-factory.db` that is a build moving work items
between lanes, which is exactly the class of thing the store-of-record rule and the loop's own
"a build may not decide anything" reading of this system exist to prevent.

**The mitigation is one environment variable, and it was verified.** The document
generator honours the app's own configuration, so pointing the store elsewhere for
the generation step is enough:

```bash
Factory__DatabasePath="$(mktemp -d)/agent-factory.db" dotnet build src/agent-factory/agent-factory.csproj
```

Re-run with `data/` absent and the OpenAPI cache cleared: the build logged
`Serving 1 project(s) from ...\factories` again — it still reads the project
directory, and still logs credential *names* and never values, which is the host-writes rule
holding — but `data/` was **not** created and the 45,056-byte database appeared at
the temporary path instead. The project directory read is a nuisance rather than a
hazard; the database write is the one that needs pinning.

Microsoft's own remedy for the general case is the documented
`Assembly.GetEntryAssembly()?.GetName().Name != "GetDocument.Insider"` guard around
service registration. That is a defensible alternative and it is more invasive here,
because it would guard the store registration itself; the environment variable keeps
`FactoryApp` unchanged and belongs in the build script, not in the composition root.

### The document as it stands is empty where the Board needs it

The emitted document is structurally correct — all eight routes, the two
strongly-typed request bodies as `components.schemas`, correct path parameters — and
**none of the response shapes**:

```json
"/api/board": { "get": { "responses": { "200": { "description": "OK" } } } },
...
"components": { "schemas": {
    "DecisionRequest": { ... },
    "ProjectWriteRequest": { ... } } }
```

No `BoardView`. No `WorkItemDetailView`. No `CredentialsView`. Nothing.

The cause is visible in `FactoryApp.cs`. Every read returns
`Microsoft.AspNetCore.Http.Results.Json(view)` — that is an `IResult`, and an
`IResult` has no signature for the framework to read a type out of. The two request
bodies got schemas for the opposite reason: they come from typed *parameters*
(`Api.DecisionRequest request`), which ASP.NET Core infers automatically.

Microsoft states the two ways out, both on the page above: return `TypedResults<T>`
("automatically includes the response type metadata for the endpoint") or annotate
with `.Produces<T>(StatusCodes.Status200OK)` ("populates metadata for the targeted
type under a `200` status code and an `application/json` content type").

**This is the real prerequisite for #80.** Eight `.Produces<T>()` annotations in
`FactoryApp.cs` — one per route, plus the 400 and 204 branches where the factory
already returns them — and the document becomes worth generating from. Until then any
generator will faithfully reproduce an empty 200. It is also a change to the file
the board-renders-the-judgement rule says "holds no policy of its own and resolves the already-registered
singletons", so it belongs in that ticket's blast radius, not in a codegen ticket.

## 2. The three generators, compared

| | `openapi-typescript` | Orval | Kiota |
| --- | --- | --- | --- |
| Latest | 7.13.0 | 8.43.0 | 1.36.1 |
| Output for this document | 8,862 B, types only | 29,085 B endpoints + 3 model files, 341+ lines of runtime | 664 lines across 13 files |
| Runtime deps added | **none** | `@tanstack/react-query` (+ warns it cannot detect the version and assumes v4) | `@microsoft/kiota-bundle` at **1.0.0-preview.110** |
| OpenAPI 3.1 | yes, stated in the feature list | yes (validates with `@scalar/openapi-parser` in v8) | yes, via `Microsoft.OpenApi.NET` |
| Relative base URL | n/a — emits no URLs | **yes**, emitted `return \`/api/board\`` | **no** — warns twice that no `servers` entry is present and "the base url will need to be set" |
| Next `output: 'export'` | irrelevant, no runtime | fine, fetch + React Query are browser code | fine in principle |
| Maturity | stable, MIT, types-only by design | active, v8 in RC→stable through 2025-26 | **TypeScript generation and HTTP both "in preview"** |

### `openapi-typescript`

<https://openapi-ts.dev/introduction>. Types only, and says so as the headline
feature: "Generate **runtime-free types** that outperform old school codegen". No
Java, no `node-gyp`, no running OpenAPI server. Output for this repository's actual
document is 8,862 bytes of `interface paths` / `components` and **zero** lines of
runtime code.

It is worth knowing that its own project ships the client half too —
`openapi-fetch` and `openapi-react-query`, linked from the same sidebar — so
"types only" is a choice within one tool's ecosystem rather than a dead end. This
matters because it means the decision can be revisited without a migration.

Determinism, checked by generating twice and hashing: identical.
`openapi-typescript` 7.13.0 produced byte-identical output on two runs.

### Orval

<https://orval.dev/docs/overview>. Generates request functions, React Query hooks, and
optionally MSW mocks. v8 changed the default HTTP client from axios to fetch
(<https://orval.dev/docs/versions/v8>), which is the right default for a browser-only
static export.

The relative-URL question is settled in its favour by observation, not by
configuration guessing: with `baseUrl: ''` the generated URL builder returned

```ts
export const getGetApiBoardUrl = () => { return `/api/board` }
```

which is exactly what the Board's hand-written `fetchBoard` already does
(`admin/lib/board.ts:169`) and exactly what a same-origin static export needs. The
docs also offer `baseUrl.runtime` for environment-driven resolution
(<https://orval.dev/docs/guides/set-base-url>) — unnecessary here.

Against it, three concrete frictions, all observed:

- **The payload is runtime code for an empty schema.** For a document whose every
  response is `data: void`, Orval produced a 29,085-byte endpoints file containing a
  `fetch` wrapper, a TanStack `queryOptions` factory, a `withQueryKey` helper and a
  `useGetApiBoard` hook per route. That is a client library bought to call eight
  endpoints, seven of which the Board already calls with six lines each.
- **It warned it could not do its job properly.** `board - ⚠️ Could not determine the
  installed @tanstack/react-query version, so hooks are generated for v4.` v5 is a
  different option-object type. Correcting it needs `override.query.version: 5` or a
  pointed-at `package.json` — configuration that exists only because a generator is
  guessing.
- **It installs into a pnpm 11 app, and pnpm writes into a gate's file.** `pnpm add -D
  orval` pulled `esbuild`, whose postinstall pnpm blocks by default, and pnpm
  responded by writing into `admin/pnpm-workspace.yaml`:

  ```diff
  +allowBuilds:
  +  esbuild: set this to true or false
   # Config-only: this is a flat single-app repository, not a pnpm workspace. The
   # file exists because pnpm 11 keeps every setting other than registry and auth in
   # it, and because the pin gate reads it as the second copy of the design-system
  ```

  That is the file `pin` reads as "the second copy of the design-system version". The
  gate produces no finding — it greps that file for `@nanisoft/prism-tokens` only —
  but an install step quietly editing a file under a gate's authority is precisely
  what "kept out of the way of the consumer gates" is asking about.

- Also: v8 is ESM-only and "Node.js 22.18 or higher required"
  (<https://orval.dev/docs/versions/v8>), while `admin/package.json` declares only
  `"node": ">=22"`.

Determinism: identical across two runs.

### Kiota

Not viable here, and it says so itself. First-party, from the tool's own output:

```
Warning: the TypeScript language is in preview (Preview) some features are not
fully supported and source breaking changes will happen with future updates.
```

`kiota info -l TypeScript` adds: "The language TypeScript is currently in Preview
maturity level", and lists `@microsoft/kiota-bundle` at **`1.0.0-preview.110`** as a
build-time and runtime requirement. That is a preview-version runtime dependency
going into a repository whose `pin` law is "the design system is an exact version and
the token package's version is the component package's decision". The mismatch is
worth naming on its own terms.

The supported-languages table in the tool's own README
(<https://github.com/microsoft/kiota>) marks C# stable (✔) for generation,
abstractions, authentication and HTTP, and TypeScript/JavaScript in preview (🛠️) for
generation and HTTP. Its .NET-native-ness is real but it buys nothing: the consumer
here is a Next.js app, not a .NET one, so it would be the preview generator.

Two further observations:

- **Relative base URLs are not Kiota's default.** It warned twice:
  `"A servers entry (v3) ... was not present in the OpenAPI description. The root URL
  will need to be set manually with the request adapter."` and `"No server url found
  in the OpenAPI document. The base url will need to be set when using the client."`
  Same-origin is then something a caller configures rather than something generated.
- **It writes two extra files into the output tree**: `kiota-lock.json` (1,375 B of
  generator state that has to be kept current) and `.kiota.log` (a run log). Both would
  land inside `admin/lib/` and be read by the gates.

## 3. The prism-gates collision risk — the heart of the ticket

The laws live in `@nanisoft/prism-ui/gates`, not in this repository. The full source
was read: `gates/laws.mjs`, `gates/run.mjs`, and all seven gate programs, from
`admin/node_modules/@nanisoft/prism-ui/gates/`. What each one actually does, and what
a generated file does to it:

| gate | what it reads | a file in `lib/generated/` |
| --- | --- | --- |
| `stylesheet-ownership` | `app/globals.css` only — bare-element selectors competing with Prism's base, `:focus` rules, dead `var()` reads | **Structurally immune.** A `.ts` file emits no CSS. Measured: 86 rules / 271 declarations before *and* after, 0 findings. |
| `runtime-token-read` | `app/` and `lib/` recursively, for `getPropertyValue`, `getComputedStyle`, `documentElement.style.`, `style.setProperty`, plus three retired-line helpers | **Read, and clean.** File count rose 11 → 16 (the five generated files); 0 findings. Grepped both generators' output against the regex directly first: 0 hits. |
| `hidden-state` | `app/globals.css` for hiding declarations and `animation`/`@keyframes`; then `app/`, `components/`, `lib/` (its `sourceRoots` default, which `prism-gates.json` does not override) for `setAttribute('data-reveal-armed'` and `removeAttribute(...)` | **Read, and clean.** File count rose 13 → 18; 0 findings; the run still reports "passed vacuously — 0 rule(s) hide content through `[data-reveal]`". |
| `retired-line` | the whole repository as text, plus `pnpm-lock.yaml` parsed as a dependency graph for `antd` / `@ant-design/*` reachability | **Read, and clean, including the graph half.** 35 files / 256,788 B → 40 files / 333,349 B with both generators' trees installed. 0 findings. See below. |
| `pin` | `package.json` and `pnpm-workspace.yaml` only | **Unaffected.** Adding `openapi-typescript` and `orval` as devDependencies and `@tanstack/react-query` as a dependency changed neither the exact `@nanisoft/prism-ui` pin nor the absence of `@nanisoft/prism-tokens`. |
| `links` | `out/` — every emitted route and anchor | **Unaffected.** A generator adds no route. 10 routes / 42 anchors, 0 findings, identical to the baseline. |
| `pack-boundary` | `out/` for `[data-pack]` elements, resolved against `scripts/pack-map.json` | **Unaffected.** 10 documents, 0 boundaries, 0 findings. Note the map declares `regions: {}` and `secondPackRegions: []` — a map that says zero is a decision, so *any* element that ever rendered a second pack would be an `undeclared-region` finding. Nothing a generator emits renders one. |

**All seven gates: green, with both a types-only output and a full Orval React Query
client in `admin/lib/generated/`, and the real lockfile carrying both trees.**

### The retired-line graph half, specifically

This was the most plausible failure and it was checked directly rather than argued.
Two independent walks of the real trees:

- A traversal of `openapi-typescript` + `orval` + `@tanstack/react-query` over 130
  packages found **zero** `antd` / `@ant-design/*` packages.
- `retired-line`'s own lockfile parser then read `admin/pnpm-lock.yaml` — the file as
  it stood with those three installed — and reported 0 findings. The gate reads the
  lockfile as a graph rather than grepping it, because a base64 integrity hash
  contains the characters a package-specifier pattern admits; a grep here would have
  been the wrong kind of evidence.

### The three risks the ticket named, answered directly

**Does a generated client put state somewhere `hidden-state` considers hidden?** No,
and the reason is structural rather than lucky. `hidden-state`'s subject is a
**CSS-authored** hidden state: `opacity: 0`, `visibility: hidden`, `display: none` in
the app's own sheet, with an exit that is either an escapable condition or a
script-armed ancestor attribute with exactly one writer. A generated client is not in
that chain — it sets no `data-reveal-armed`, and the gate counts the **write**
(`setAttribute(...)` on a comment-blanked line) rather than the name, so even a
generated file that mentioned the attribute in a comment would not be counted.

The honest limit, and it is one worth carrying into #80: the gate reads the **sheet**,
not JSX. A `style={{ display: 'none' }}` or a `hidden` attribute written in a
component is not something any gate in this kit can see — the kit's own README says
"a stylesheet half is not a rendered half". That limit exists today and codegen does
not change it.

**Is a generated file inside the pack boundary acceptable?** There is no pack boundary
to be inside. `scripts/pack-map.json` declares `"ground": "lavender"`,
`"defaultMode": "dark"`, `"regions": {}`, `"secondPackRegions": []`, and the run
confirms "0 of 0 boundary/boundaries sit on a mark". `pack-boundary` reads the
*emitted HTML*, never source, so a generated module cannot reach it. The one thing
worth saying is that the pack map is a zero that must stay zero deliberately: a
generated component that rendered its own `data-pack` would be an
`undeclared-region` finding, which is the gate working.

**`pin` and `retired-line`?** Covered above: `pin` reads two config files and neither
changed meaning; `retired-line` read 5 more files, ~76 KB more text, and a dependency
graph three times the size, and found nothing.

### Where the generated output should live, and why it matters to the gates

**`admin/lib/generated/`, and the reason is a gate one.** `runtime-token-read`'s
`sourceRoots` is declared in `prism-gates.json` as `["app", "lib"]`, and
`hidden-state`'s default is `["app", "components", "lib"]`. A top-level
`admin/generated/` directory would be read by **neither**, and only by
`retired-line` (which walks the whole repository). Putting generated code inside a
declared root keeps it inside the gate kit's sight, which is the same argument
`gates/README.md` makes about what a consumer holds and what the package holds.

The cost is honest: 16 and 18 files read instead of 11 and 13, against floors of 2 and
2. No risk. The benefit is that a future edit to a generator's output that *did*
introduce a runtime token read would fail the build rather than passing unnoticed.

One thing outside the gates entirely: `prism-gates` runs with `root = process.cwd()`,
i.e. `admin/`. This note lives in repository-root `docs/research/`, which no gate in
this kit reads. That is a fact about where prose goes, not a loophole to rely on.

## 4. Types only, or a client?

**Types only. `openapi-typescript`, into `admin/lib/generated/`, and the seven (five
files, ten functions) hand-written interfaces stay where they are.**

The case, in the order the argument actually runs:

1. **The Board's fetches are the Board's own decisions, not the factory's.** the board-renders-the-judgement rule
   is explicit that the app "holds no policy of its own" and re-decides nothing. The
   things each hand-written function does beyond `fetch` are app policy: `cache:
   'no-store'` because "every read reflects the machine now" (`lib/board.ts:163`),
   `encodeURIComponent` on every id that arrived from a route segment
   (`lib/work-items.ts:133`), turning a non-2xx into `the factory answered ${status}`
   so the renderer has a string to show. A generated client owns the second of those
   by default and would have to be overridden on the first. Overriding a generator's
   behaviour on every call is worse than writing the call.

2. **Every read wants `cache: 'no-store'`, and Orval does not do that.** The Board
   refreshes itself; a cached read "would be a claim about a moment that has passed"
   — the stated reason for the flag in all five files. A caching client is the wrong
   default for a live Kanban board, and correcting it is per-call configuration in
   generated code that a human does not own.

3. **React Query is a second state machine, and this system is careful about those.**
   the board-renders-the-judgement rule's whole argument is that "a board that derived its own swimlanes would be
   a second state machine free to disagree with the loop about the same work item". A
   query cache decides when data is stale and when to refetch; the Board currently
   decides that itself and renders three states the factory distinguishes
   (`Reading the factory`, `the factory answered 500`, the loaded board —
   `test/board.test.tsx:351-387`). Introducing a cache is a design decision about
   staleness, and it is not this ticket's decision to make.

4. **The generated client is 4× the bytes for zero payload today.** 8,862 B of types
   versus 29,085 B of hooks, on a document where all eight responses are `data: void`.
   That ratio improves once `.Produces<T>()` lands — but it improves in the *types*,
   which is where the value is anyway.

5. **Types-only is the smaller commitment, and it is reversible in one line.** The
   `openapi-typescript` project ships `openapi-fetch` and `openapi-react-query` itself
   (<https://openapi-ts.dev/introduction>, sidebar), so moving to a generated client
   later does not mean a different ecosystem. It does mean accepting the cache, the
   overrides and the runtime dependency — all three of which should be argued for on
   their own day.

### What codegen actually buys here, stated narrowly

Not "the types are correct today" — they are; `admin/lib/decisions.ts` and
`Api/DecisionView.cs` agree field for field. What it buys is that **the seven
interfaces stop being a second copy that can drift, and become aliases of the
factory's own schema**, so a rename on the .NET side is a `tsc` failure instead of a
runtime `undefined` on a reviewer's screen.

Concretely, `lib/board.ts`'s `BoardView` becomes:

```ts
import type { components } from './generated/openapi';

export type BoardView = components['schemas']['BoardView'];
```

The prose in each file stays — the field comments are the Board's design record and
the board-renders-the-judgement rule's "the app is a thin renderer" argument depends on them — and the fetch
functions stay exactly as they are.

## 5. Mechanics

### Where the output lives, and whether it is committed

**Commit `admin/lib/generated/openapi.d.ts`. Do not generate it in `pnpm build`.**

Against generating in the build:

- The input is not there. `openapi.json` lands in `obj/`, which is gitignored, so
  `pnpm build` would need `dotnet build` first — coupling a pnpm build to a .NET
  build, which the one-process rule's "one process" shape does not want and which the two-language
  repository would have to orchestrate.
- The generation step **writes to the store of record** (§1). Putting it in a build
  step means every build touches `data/agent-factory.db` unless the environment
  variable is set, and a missed variable is a silent data mutation rather than a
  failed build.
- The Board builds independently today, and keeping it that way is worth more than
  saving a checked-in file.

Against committing: nothing, provided drift is caught. `openapi-typescript` output is
deterministic (verified by double generation and hash), so a committed file has no
diff churn.

`openapi.json` itself: commit it too, or point
`<OpenApiDocumentsDirectory>openapi.json</OpenApiDocumentsDirectory>` at the project
and let it be a build artefact read by a checked-in step. Committing both makes the
contract reviewable in a diff, which is the same argument the repository makes about
`workers/` and `factories/`. **This is a judgement call and #80 should settle it.**

### Drift detection

The check that fits this repository is the one the deriver already demonstrates:
regenerate and diff, in CI, and fail on any difference.

```jsonc
// admin/package.json
{
  "scripts": {
    "openapi": "dotnet build ../src/agent-factory/agent-factory.csproj && openapi-typescript ../src/agent-factory/obj/agent-factory.json -o lib/generated/openapi.d.ts",
    "check:openapi": "pnpm openapi && git diff --exit-code -- lib/generated"
  }
}
```

`openapi-typescript` is deterministic, so `git diff --exit-code` is a sound drift
detector and needs no checksum file. It needs
`Factory__DatabasePath` pointed at a temporary path (verified in §1) so the
regeneration cannot touch the real store.

One caveat worth stating: this detects *schema* drift, and it only fires when the
regeneration runs. A stronger variant, and the one that matches how the .NET side
already tests its API, is a `PolicyTests`-style assertion that every route in
`FactoryApp.cs` appears in `openapi.json` with a schema — which fails loudly on the
specific defect this ticket will otherwise create, an endpoint someone added without
`.Produces<T>()`.

### The vitest fixtures

`admin/test/*.tsx` render against `admin/test/fixtures/*.json`, and `board.test.tsx`
says the fixture "mirrors the real `GET /api/board` response". Today nothing enforces
that: a fixture is `unknown` to `tsc`, so a renamed field is a runtime `undefined` in
a passing test suite.

With generated types it becomes a compile error, and that is the single highest-value
thing codegen buys in this repository:

```ts
import type { components } from '@/lib/generated/openapi';
import boardFixture from './fixtures/board.json';

const fixture: components['schemas']['BoardView'] = boardFixture;
```

or `satisfies`, which keeps the literal type. Either turns the fixture from a
copy-a-test-hopes-is-right into a checked claim. The fixtures stay as data; the
*assertion* is what changes.

Two things to watch:

- `admin/tsconfig.json` has `"resolveJsonModule": true` and includes `**/*.ts`, so a
  fixture import already typechecks. No config change needed.
- The observed build already typechecks generated files: `next build` ran TypeScript
  over `lib/generated/**` with the Orval client present and passed, with `strict`,
  `noUncheckedIndexedAccess`, `noUnusedLocals` and `verbatimModuleSyntax` all on. A
  generated file that does not survive those four flags will fail the Board build —
  which is the right place for that to fail.

### What #80 should do, in order

1. Add `.Produces<T>()` to every route in `FactoryApp.cs`, including the 400 and 204
   branches. **Verify the document has response schemas before anything else** — the
   rest of this is worthless without it.
2. Add `Microsoft.AspNetCore.OpenApi` + `Microsoft.Extensions.ApiDescription.Server` to
   the Web project and `AddOpenApi()`. Do **not** `MapOpenApi()`.
3. Pin `Factory__DatabasePath` to a temporary path wherever the document is
   generated, and say so in the script that does it.
4. Add `openapi-typescript` as a devDependency; commit `admin/lib/generated/openapi.d.ts`.
5. Convert the seven hand-written interfaces to aliases over `components['schemas']`,
   keeping the prose.
6. Add the drift check, and a test asserting every route has a response schema.
7. Type the vitest fixtures.

Steps 1 and 3 are the ones that will be skipped and should not be.

## What could not be verified

Stated rather than guessed:

- **The gate kit is a private-ish package.** Everything in §3 was read from
  `admin/node_modules/@nanisoft/prism-ui/gates/*.mjs` at the pinned 1.1.0, which is
  the authoritative source the laws live in — but a future release may change a law
  or a gate's surface. `gateKit: 1` is the generation this app is held to; a law
  rewritten in a later release is a version this app has not moved to, which is
  exactly what `gateKit` exists to make visible.
- **§3 was measured on one document** — the factory's own, with empty response
  schemas. Once `.Produces<T>()` lands the generated files get substantially larger and
  the regex greps in §3 should be re-run. Types-only output cannot grow into anything
  the `runtime-token-read` regex matches, but the Orval client's *model* files could
  grow into a schema that names something odd, and `retired-line`'s
  `prismCssVarKey|prismBrandPacks|PrismThemeModeProvider` check is a name match.
- **The `pnpm-workspace.yaml` `allowBuilds` edit** is Orval-specific (esbuild). It was
  observed on pnpm 11.18.0 on Windows and reverted; it has not been tested on CI.
- **`.NET 11` changes were read but not tested.** The docs describe
  `OpenApiGenerationEnvironment` (new in .NET 11) and OpenAPI 3.2 as the .NET 11
  default. This repository is on .NET 10, so none of it applies yet.
- **The `AllowBuilds`/supply-chain posture** is pnpm's, not prism-gates'. The
  repository's existing `pnpm-workspace.yaml` comment already says pnpm 11 keeps its
  settings there; adding a generator that needs a build script widens what that file
  has to carry.
- **Kiota's preview line was not re-checked against a roadmap.** The README table and
  the tool's own warning are what is quoted; whether Microsoft intends to graduate
  TypeScript is not something this repository can verify.
