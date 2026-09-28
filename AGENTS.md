# AGENTS.md

Guidance for agents operating in this repository.

## What this repository is

`agent-factory` is the implementation of the agent factory described in
[`DESIGN.md`](DESIGN.md): a generic system that takes GitHub issues as use cases,
builds the change that answers them using code-server and OpenCode inside a fresh
container per issue, and merges the result back through a Kanban board where a
human reviews it. One issue in, one reviewed pull request out.

The public, reader-facing version of the design is published separately as the
Nexus documentation at https://nexus.nanisoft.com/docs. That is a publication
target, not a build dependency: this repository is the implementation of record.

## Commands

Not yet established. The first ticket that introduces a build defines the command
set here.

## Agent skills

### Issue tracker

Issues and specs live as GitHub issues in `NaniSoft/agent-factory`, driven with the `gh`
CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical triage roles, used verbatim: `needs-triage`, `needs-info`,
`ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout: one `CONTEXT.md` and `docs/adr/` at the repo root. See
`docs/agents/domain.md`.
