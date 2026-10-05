# agent-factory

The agent factory: a generic system that takes GitHub issues as use cases,
builds the change that answers them using code-server and OpenCode inside a
fresh worker container per round, and merges the result back to the repository
through a Kanban board where a human reviews it.

## Status

**Implemented, and running.** One .NET process polls the open issues of every
configured project, turns each into a work item on a board at port 5000, runs up
to three rounds per work item in fresh worker containers, and merges the change
on approval from the host under a credential the agent never sees. This has been
run end to end against a real repository: an issue was polled, built by an agent
in a real container, reviewed on the board, and shipped as a merged pull request.

Build and test it with the commands in [`AGENTS.md`](AGENTS.md).

```bash
docker compose up --build -d      # the factory, the board on 127.0.0.1:5000
docker build -t ghcr.io/nanisoft/agent-factory-worker:1 worker/   # the image rounds start from
```

[`DESIGN.md`](DESIGN.md) is the specification the factory is built against
(ADR-0002), and the decisions it defers on are recorded one file each in
[`docs/adr/`](docs/adr/). [`AGENTS.md`](AGENTS.md) records what is built, how it
is tested, and why each boundary is where it is.

The factory's public documentation is published separately, in the
[nexus](https://github.com/NaniSoft/nexus) repository, at
**https://nexus.nanisoft.com/docs**. That is the canonical, reader-facing version
of the same design and goes into considerably more depth. It was frozen while the
implementation ran and is reconciled in one sweep now that there is a working
factory to check it against. Read it there; use this repository for the design
record and the implementation.

## What is here

- [`DESIGN.md`](DESIGN.md) — the design: components, the per-project
  configuration model, the worker container lifecycle, the Kanban feedback loop
  and its 3-round ceiling, multi-project round-robin, observability and error
  handling, the testing strategy, deployment, and the security posture.
- [`AGENTS.md`](AGENTS.md) — what is built and how it is verified.
- [`CONTEXT.md`](CONTEXT.md) — the domain vocabulary the code and this repository
  speak.
- [`docs/adr/`](docs/adr/) — the decisions the design defers on.
- [`src/`](src/) — the .NET solution: one process holding the config loader, the
  work item store, the orchestrator, intake, the merger and the board.
- [`worker/`](worker/) — the base worker image a round starts from, its smoke
  test, and the recipe for deriving a project's own.
- [`factories/`](factories/) — one file per project, and **this repository commits
  none**: a file there is served, not sampled. The examples are in
  [`worker/examples/`](worker/examples); read [`factories/README.md`](factories/README.md)
  before adding one.
- [`docker-compose.yml`](docker-compose.yml) — one deployment for every project.
- [`.gitignore`](.gitignore) — keeps local agent and tool state, including
  `.claude/settings.local.json`, out of the repository.

The sequenced build tickets that once lived here were a working artifact of the
planning flow, not part of the design, and have been removed; they are tracked as
issues in this repository.
