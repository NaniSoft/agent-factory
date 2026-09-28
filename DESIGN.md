# Agent Factory Design

The design of the agent factory: a generic system that receives GitHub issues as
use cases, builds the change that answers them using code-server and OpenCode,
and merges the result back to the repository. One issue in, one reviewed pull
request out.

**Status: implementation under way.** This design is the specification the factory
is built against, and the decisions it defers on are recorded as ADRs in
`docs/adr/`. The factory has not shipped. The polished, public presentation of
this same design is published as the Nexus documentation and is frozen until
reconciliation (ADR-0002); see
[Where the public docs live](#where-the-public-docs-live).

## Components

Five components with narrow contracts between them.

| Component | Responsibility |
| --- | --- |
| Config loader | Reads and validates per-project YAML; exposes values to every other component. |
| Issue poller | Reads open issues from each configured repository and converts them to work items. |
| Orchestrator + workers | Accepts work items, starts worker containers, drives the build, tracks rounds. |
| Kanban board | Serves the board at port 5000. It holds no board state of its own. |
| Merger | Opens and merges the pull request from the host when a work item is approved. |

These are five components in one .NET process, not five services: the board is an
ASP.NET Core endpoint on port 5000 served by that same process, and the
components call each other as ordinary method calls (ADR-0003). The board reads
the records the orchestrator writes. There is no board-side copy of the truth,
and in one process that is the absence of a second copy rather than a
consistency property to maintain.

The orchestrator is a hand-rolled deterministic state machine. It runs no
Microsoft Agent Framework and hosts no agents of its own — the agent inside the
worker container does all the agentic work (ADR-0005). The orchestrator does not
reach into the container to run commands itself: it hands a work item to
NOpenCode, which drives OpenCode inside the worker container and returns
structured results. NOpenCode is our own interface, not a package of that name
(ADR-0004). Keeping that boundary is what allows the orchestrator's policy to be
tested against a fake NOpenCode.

## Configuration

The system is configured with a YAML file per project, held in the `factories/`
directory — one file per project, nothing else. Adding a project to the factory
is adding a file to that directory: no component changes, no rebuild.

Each project's file names four things:

- **Git repository URL** — where issues are polled from and pull requests opened.
- **API keys** — the credentials the project's builds need, referenced by name.
- **LLM provider** — which provider this project's build agents use.
- **Worker Docker image** — the image each round's worker container starts from.

All of them are per project. The factory's own behaviour — round ceiling,
timeouts, merge policy, retry strategy — is not configurable per project; it is
the factory's, identical everywhere.

The config loader validates a project file before anything downstream sees it.
A file that fails validation keeps its project out of the rotation rather than
failing the whole factory later, mid-run, in a worker container. Loaded
configuration is exposed to the issue poller, the orchestrator, and the board.

## From GitHub issue to work item

The issue poller reads open issues from the configured repository. Every open
issue is a candidate work item — there is no label or assignee filter, and the
board is where a human decides what is worth building (ADR-0007). Each issue
becomes a work item with a title, a description, and an initial **Backlog**
status. Work items are persisted in SQLite, the factory's store of record, so the
board and the orchestrator read the same record rather than each keeping its own
state (ADR-0009).

## The worker container

Every round gets a fresh, isolated worker container. One exists for exactly one
round, and nothing survives it — no cache, no working tree, no state from a
previous run or from another project's run. The container's lifetime and the
round boundary are the same thing (ADR-0001).

Inside the container, code-server and OpenCode build and test the change from
the issue. The project's own tests run inside the same container, on the same
tree the build produced. Files changed, commands run, and test outcomes are
derived by the factory observing the container rather than from the agent's own
account of them (ADR-0011), and are persisted with the work item, so the reviewer
sees what happened rather than a summary of it.

## The Kanban board and the feedback loop

The board is served at **port 5000** with auto-refresh, and it is the only
surface where work is approved. It has five swimlanes:

**Backlog** → **Frontier** → **In Progress** → **Review** → **Done**

A work item in Review offers three decisions:

- **Approve** — triggers the auto-merge: the factory opens the pull request and
  merges it.
- **Request changes** — the work item returns to the worker for another round
  with the feedback attached, up to a maximum of **3 rounds** per work item.
- **Reject** — the work item moves to Rejected, and that is final.

**Rejected** and **Escalated** are two distinct states with two distinct causes:
a human decline, and a failure. The board renders both.

**Feedback timeout triggers auto-merge** after a threshold: a work item left in
Review past that point is merged rather than holding the pipeline. Auto-merge is
the design's deliberate answer to a reviewer who is not watching; it is stated
plainly so it can be governed, and the merge is always a revertible pull
request.

The 3-round ceiling prevents the failure mode an unbounded feedback loop has:
the agent and the reviewer disagreeing forever while the factory keeps paying
for containers. When the third round comes back with feedback still outstanding,
the work item escalates. It is never merged on exhaustion (ADR-0008).

Escalation is a parking state, not a terminal one: a human can still merge or
reject the work item from the board. The asymmetry is deliberate. Ignoring a
work item merges it; actively rejecting it protects the repository. The feedback
timeout is the more dangerous of the two paths, and it is rendered on the board
for that reason.

## Multi-project round-robin

Multiple projects configured in `factories/` are polled and processed in
round-robin order. Each pass gives every project a turn, so one noisy
repository cannot starve the rest.

Rounds for different projects do not block each other: a long build in one
project does not stop another's issue from starting. Each round still gets a
fresh worker container regardless of project, so concurrency is bounded by the
container budget rather than a shared working tree or a shared lock. The board
can group or filter work items by project.

## Observability and error handling

- **Structured logging** across all components — the poller, the worker, the
  board, and the merger — so "what happened to this work item" is answerable
  without reading prose, and concurrent runs do not blur together.
- **Metrics** for issues processed, builds succeeded and failed, and feedback
  rounds per work item.
- **Retry strategy** for transient failures only, such as GitHub API calls and
  container starts. A build that fails its tests is not a transient failure, and
  retrying it is an expensive way to hide the result.
- **Escalation** for work items that fail permanently or exhaust their rounds.
  Escalated work items are visible on the board, alongside failed builds and
  round counts.

Nothing in the failure path ends in silence: every outcome is a state the board
renders.

## Testing strategy

Four layers:

- **Unit** — configuration loading and validation, issue-to-work-item
  conversion, the feedback state machine, and retry and escalation logic.
- **Integration** — Testcontainers, running a real worker container that starts
  and actually builds; plus the GitHub-facing path against the GitHub API,
  mocked where determinism matters and real where the API's shape is what is
  under test.
- **End-to-end** — a work item walked through the whole loop (issue in, build,
  review decision, merge), covering approve-to-merge and two or more configured
  projects.
- **Chaos** — failure injected deliberately, asserting that the failure path
  behaves as designed: nothing ends in silence.

## Deployment

The factory deploys with **Docker Compose**. One application container serves the
board at port 5000 and hosts every component above; the only other service is
the Docker runtime it starts worker containers against (ADR-0003). One
deployment serves every configured project; there is no per-project deployment.
Worker containers are not long-running members of the deployment — they start
on demand and are removed when the round ends.

## Security and secrets

Secrets are carried as **environment variables**, and project configuration
references them by name rather than containing them, which keeps
`factories/*.yaml` committable and makes rotation an environment change.
Credentials are scoped to the narrowest set that still works, and are per
project, so a leaked or over-broad credential has a blast radius of one project.

The factory assumes its workloads are untrusted — they are written by agents —
so **rootless Docker** is a named security consideration: one fresh container
per round, scoped credentials, no root daemon. The requirement holds where the
factory runs for real; local development on Docker Desktop is a bounded
deviation with named compensating controls (ADR-0012).

## Where the public docs live

This document is the internal design record, and it is the specification the
factory is built against. The public, published version of the same design is
the Nexus documentation at **https://nexus.nanisoft.com/docs** (source in the
`nexus` repository), which covers the same architecture, concepts, configuration,
operations, and reference material in more depth. It is frozen while the
implementation runs and is reconciled in one sweep at the first working
milestone (ADR-0002).
