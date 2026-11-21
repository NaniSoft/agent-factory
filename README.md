# Agent Factory

An autonomous agent-fleet orchestrator in C#, with **opencode** as the engine:
GitHub issues in, agent sessions working in fresh worktrees, pull requests out,
and a React board over the top.

The factory polls every enrolled repo (or every eligible repo of an enrolled
org), turns an opted-in issue into a **run**, walks that run through an ordered
**pipeline** of agent sessions, checks that the work was committed, and lands it
as a pull request. A human reviews the pull request; the board is the fleet's
read-only window and its writable configuration surface.

## What is here

```
src/AgentFactory.App/
  Fleet/        the engine: polling, runs, pipelines, profiles, git, GitHub, triage
  Api/          the JSON surface the board renders, and the static-export server
  ClientApp/    the React board (Next static export, designed with Prism)
tests/          the store, planner and helper tests
deploy/k8s/     the all-in-one pod, its config map and its durable volume
docs/           the architecture decision records and the research behind them
```

- **Fleet** watches the targets, runs the queue round-robin, and keeps one
  opencode serve alive. It drives `opencode`, `gh` and `git` on the host.
- **Pipelines** are ordered phases. Every phase has its own full prompt
  template, skill allow-list and optional agent-profile pin (profile and model
  together, or neither).
- **Profiles** describe how an agent authenticates: a flavour
  (`opencode-go`), an optional credential
  and the model palette that bounds every phase pin.
- **Targets** are the repos and orgs the fleet works. An org scan materializes
  eligible repos into targets; per-target intake, pipeline and definition of
  done are editable.
- **The board** is a React app in `ClientApp`, served by the same .NET process at
  the root, reading the JSON API under `/api`.

## Build and run

.NET 10 is required.

```sh
dotnet build agent-factory.slnx
dotnet test  agent-factory.slnx

dotnet run --project src/AgentFactory.App
```

The app answers on <http://localhost:5200> (the launch profile) and serves the
board at `/`. The JSON surface is `/api`.

The board is a static Next export. Build it before the .NET publish, or run it
in development beside the app:

```sh
cd src/AgentFactory.App/ClientApp
pnpm install
pnpm build          # static export to ClientApp/out, copied into wwwroot by the csproj
pnpm dev            # dev server on :3000, proxying /api to http://127.0.0.1:5200
```

The full check set for the board is:

```sh
pnpm lint
pnpm typecheck
pnpm test
pnpm check          # the Prism consumer gates; run after pnpm build
```

## Configuration

Static engine settings live in `src/AgentFactory.App/appsettings.json` and bind
from the `Fleet` section (the same keys with `Fleet__` prefix work as pod
environment variables). Runtime configuration — pipelines, targets, profiles,
the utility and fallback bindings, and the paused flag — lives in
`data/config.json`, is hot-reloaded on a hand edit, and is edited from the board.

A credential **name** and a credential **value** are separate facts: a profile
may carry a literal key, an opencode OAuth envelope, or an `{env:NAME}`
reference resolved at injection. The GitHub write token is resolved on the host
and never reaches an agent session.

Data and worktrees are gitignored:

```
data/                 the config document, runs.json and every run log
data/clones/          one clone per enrolled repo
data/worktrees/       one fresh worktree per run attempt
```

## Deployment

`Dockerfile` builds the board, publishes the app, and packages the runtime with
`opencode`, `gh`, `git`, the .NET SDK and Node for per-target definitions of
done. `deploy/k8s/` runs one all-in-one pod on a single durable volume; see
[`deploy/k8s/README.md`](deploy/k8s/README.md).

## Documentation

- [`GLOSSARY.md`](GLOSSARY.md) — the ubiquitous language. Its *avoid* lists are
  binding.
- [`docs/adr/`](docs/adr) — the decisions that outlived the code that first
  carried them.
- [`docs/research/`](docs/research) — the evidence behind the container and
  opencode choices.
- [`src/AgentFactory.App/ClientApp/README.md`](src/AgentFactory.App/ClientApp/README.md)
  — the board's own build and design rules.
