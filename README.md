# agent-factory

The agent factory: a generic system that takes GitHub issues as use cases,
builds the change that answers them using code-server and OpenCode inside a
fresh worker container per round, and merges the result back to the repository
through a Kanban board where a human reviews it.

## Status

**Implementation is under way.** This repository began as the design and now
also carries the implementation. The .NET solution is in `src/`, and it is
early: the projects are scaffolds built against the design, not a working
factory. Nothing here polls an issue, runs a round, or merges anything yet.

[`DESIGN.md`](DESIGN.md) is the specification the factory is built against
(ADR-0002), and the decisions it defers on are recorded one file each in
[`docs/adr/`](docs/adr/).

The factory's public documentation is published separately, in the
[nexus](https://github.com/NaniSoft/nexus) repository, at
**https://nexus.nanisoft.com/docs**. That is the canonical, reader-facing version
of the same design and goes into considerably more depth. It is frozen while the
implementation runs and is reconciled in one sweep at the first working
milestone. Read it there; use this repository for the design record and the
implementation.

## What is here

- [`DESIGN.md`](DESIGN.md) — the design: components, the per-project
  configuration model, the worker container lifecycle, the Kanban feedback loop
  and its 3-round ceiling, multi-project round-robin, observability and error
  handling, the testing strategy, deployment, and the security posture.
- [`CONTEXT.md`](CONTEXT.md) — the domain vocabulary the code and this repository
  speak.
- [`docs/adr/`](docs/adr/) — the decisions the design defers on.
- [`src/`](src/) — the .NET solution, under construction.
- [`.gitignore`](.gitignore) — keeps local agent and tool state, including
  `.claude/settings.local.json`, out of the repository.

The sequenced build tickets that once lived here were a working artifact of the
planning flow, not part of the design, and have been removed; they are tracked as
issues in this repository.
