# AGENTS.md — Agent Factory

## What this repository is

`agent-factory` takes a repository's GitHub issues and turns them into reviewed
pull requests: an issue in, an agent-built change out, with a human on the board
in between. The factory drives opencode sessions inside a fresh git worktree per
run attempt, and the host owns the push and pull-request flow.

## Commands

.NET 10 and Node 22. Run from the repository root.

```sh
dotnet build agent-factory.slnx
dotnet test  agent-factory.slnx

dotnet run --project src/AgentFactory.App      # app and board on http://localhost:5200

cd src/AgentFactory.App/ClientApp
pnpm install
pnpm dev                                       # board on :3000, /api proxied to :5200
pnpm build                                     # static export into ClientApp/out
pnpm lint && pnpm typecheck && pnpm test
pnpm check                                     # the Prism consumer gates; run after build
```

## The architecture

One .NET process and one React export.

- `Fleet/` holds the engine. `FleetRunner` is the loop: keep the opencode serve
  up, scan orgs, poll targets, cancel un-enrolled queued runs, and drain the
  queue round-robin. `RunExecutor` walks a run through its pipeline.
  `ConfigStore` and `RunStore` are the two JSON stores of record.
- `Api/AgentFactoryApi.cs` is the whole JSON surface the board reads. It holds
  no policy: it resolves the same singletons the loop resolves.
- `Api/ClientExport.cs` serves the React export at the root and keeps `/api`
  from ever being answered with an HTML page.
- `ClientApp/` is a flat Next static export designed with
  `@nanisoft/prism-ui`. It is one ground (lavender, dark) wearing the design
  system's console shell: one sheet, a rail, and a top bar. Its own classes are
  layout only.

## Load-bearing facts

- **The host owns git and the PR.** An agent session works inside its worktree,
  commits, and stops. The host checks the commits, strips the fleet-local
  `opencode.json`, pushes the branch and opens the pull request.
- **A run retries once, as a full fresh pipeline pass.** A retry is not a
  resume.
- **A pinned phase is atomic.** Profile and model together, or neither. The
  model must be in the profile's palette; a dangling pin fails the run fast.
- **The catalog and credentials are boot-time snapshots of the serve.** A
  changed credential costs a serve restart at run start; the board's manual
  restart is for an idle fleet.
- **The board is served by the same process as the fleet** and holds the fleet's
  only write surface. Keep it on a trusted network; it is not a public site.

## Where words live

- [`GLOSSARY.md`](GLOSSARY.md) is the ubiquitous language. Its *avoid* lists are
  binding. A **Run** is not a job, a **Phase** is not a stage, and the AI a run
  drives is the **agent**.
- [`README.md`](README.md) is what the product is and how it is built.
- [`docs/adr/`](docs/adr) records the decisions that outlived the code.
- The board's own rules are in
  [`src/AgentFactory.App/ClientApp/AGENTS.md`](src/AgentFactory.App/ClientApp/AGENTS.md).
