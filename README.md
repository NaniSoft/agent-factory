# agent-factory

Design repository for the agent factory — a generic system that takes GitHub
issues as use cases, builds the change that answers them using code-server and
OpenCode inside a fresh container per issue, and merges the result back to the
repository through a Kanban board where a human reviews it.

## Status

**This is a design-only repository. There is no code here, no build, and no test
suite, and the factory has not been implemented.** The contents are the design
and nothing more.

The factory's public documentation is published separately, in the
[nexus](https://github.com/NaniSoft/nexus) repository, at
**https://nexus.nanisoft.com/docs**. That published documentation is the
canonical, reader-facing version of this same design and goes into considerably
more depth. Read it there; use this repository for the design record itself.

## What is here

- [`DESIGN.md`](DESIGN.md) — the design: components, the per-project
  configuration model, the worker container lifecycle, the Kanban feedback loop
  and its 3-round ceiling, multi-project round-robin, observability and error
  handling, the testing strategy, deployment, and the security posture.
- [`.gitignore`](.gitignore) — keeps local agent and tool state, including
  `.claude/settings.local.json`, out of the repository.

The sequenced build tickets that once lived here were a working artifact of the
planning flow, not part of the design, and have been removed.
