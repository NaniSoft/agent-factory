# Agent Factory Design

The design of the agent factory: a generic system that receives GitHub issues as
use cases, builds the change that answers them using code-server and OpenCode,
and merges the result back to the repository. One issue in, one reviewed pull
request out.

**Status: design stage.** Nothing in this design is implemented. This repository
holds the design and nothing else — there is no source code here, and the
factory has not shipped. The polished, public presentation of this same design
is published as the Nexus documentation; see
[Where the public docs live](#where-the-public-docs-live).

## Components

Five components with narrow contracts between them.

| Component | Responsibility |
| --- | --- |
| Config loader | Reads and validates per-project YAML; exposes values to every other component. |
| Issue poller | Reads open issues from each configured repository and converts them to work items. |
| Orchestrator + workers | Accepts work items, starts worker containers, drives the build, tracks rounds. |
| Kanban service | Holds board state and serves the board at port 5000. |
| Merger | Opens and merges the pull request when a ticket is approved. |

The orchestrator is a .NET service built on the Microsoft Agent Framework, which
sequences the agents involved in a build. The orchestrator does not reach into
the container to run commands itself: it hands a work item to NOpenCode, which
drives OpenCode inside the worker container and returns structured results.
Keeping that boundary is what allows the orchestrator's policy to be tested
against a fake NOpenCode.

## Configuration

The system is configured with a YAML file per project, held in the `factories/`
directory — one file per project, nothing else. Adding a project to the factory
is adding a file to that directory: no component changes, no rebuild.

Each project's file names four things:

- **Git repository URL** — where issues are polled from and pull requests opened.
- **API keys** — the credentials the project's builds need, referenced by name.
- **LLM provider** — which provider this project's build agents use.
- **Worker Docker image** — the image each issue's worker container starts from.

All of them are per project. The factory's own behaviour — round ceiling,
timeouts, merge policy, retry strategy — is not configurable per project; it is
the factory's, identical everywhere.

The config loader validates a project file before anything downstream sees it.
A file that fails validation keeps its project out of the rotation rather than
failing the whole factory later, mid-run, in a worker container. Loaded
configuration is exposed to the issue poller, the worker, and the board.

## From GitHub issue to work item

The issue poller reads open issues from the configured repository. Each issue
becomes a work item with a title, a description, and an initial **Backlog**
status. Work items are persisted, so the board and the orchestrator read the
same record rather than each keeping its own state.

## The worker container

Every issue gets a fresh, isolated container environment. A worker container
exists for exactly one build attempt, and nothing survives it — no cache, no
working tree, no state from a previous run or from another project's run.

Inside the container, code-server and OpenCode build and test the change from
the issue. The project's own tests run inside the same container, on the same
tree the build produced. Files changed, commands run, and test outcomes come
back as structured results and are persisted with the work item, so the reviewer
sees what happened rather than a summary of it.

## The Kanban board and the feedback loop

The board is served at **port 5000** with auto-refresh, and it is the only
surface where work is approved. It has five swimlanes:

**Backlog** → **Frontier** → **In Progress** → **Review** → **Done**

A ticket in Review offers three decisions:

- **Approve** — triggers the auto-merge: the factory opens the pull request and
  merges it.
- **Request changes** — the ticket returns to the worker for another build with
  the feedback attached, up to a maximum of **3 rounds** per ticket.
- **Reject** — the ticket moves to a dead/rejected state.

**Feedback timeout triggers auto-merge** after a threshold: a ticket left in
Review past that point is merged rather than holding the pipeline. Auto-merge is
the design's deliberate answer to a reviewer who is not watching; it is stated
plainly so it can be governed, and the merge is always a revertible pull
request.

The 3-round ceiling prevents the failure mode an unbounded feedback loop has:
the agent and the reviewer disagreeing forever while the factory keeps paying
for containers. When the third round comes back with feedback still outstanding,
the loop closes rather than continuing.

## Multi-project round-robin

Multiple projects configured in `factories/` are polled and processed in
round-robin order. Each pass gives every project a turn, so one noisy
repository cannot starve the rest.

Workers processing issues from different projects do not block each other: a
long build in one project does not stop another's issue from starting. Each
issue still gets a fresh container environment regardless of project, so
concurrency is bounded by the container budget rather than a shared working tree
or a shared lock. The board can group or filter tickets by project.

## Observability and error handling

- **Structured logging** across all components — the poller, the worker, the
  board, and the merger — so "what happened to this ticket" is answerable
  without reading prose, and concurrent runs do not blur together.
- **Metrics** for issues processed, builds succeeded and failed, and feedback
  rounds per ticket.
- **Retry strategy** for transient failures only, such as GitHub API calls and
  container starts. A build that fails its tests is not a transient failure, and
  retrying it is an expensive way to hide the result.
- **Dead-letter escalation** for tickets that fail permanently or exhaust their
  rounds. Escalated tickets are visible on the Kanban board, alongside failed
  builds and round counts.

Nothing in the failure path ends in silence: every terminal state is a state
the board renders.

## Testing strategy

Four layers:

- **Unit** — configuration loading and validation, issue-to-work-item
  conversion, the feedback state machine, and retry and dead-letter logic.
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

The factory deploys with **Docker Compose**. The deployment brings up the
orchestrator with its worker pool, the Kanban service publishing the board at
port 5000, and the Docker runtime they start containers against. One deployment
serves every configured project; there is no per-project deployment. Worker
containers are not long-running members of the deployment — they start on
demand and are removed when the attempt ends.

## Security and secrets

Secrets are carried as **environment variables**, and project configuration
references them by name rather than containing them, which keeps
`factories/*.yaml` committable and makes rotation an environment change.
Credentials are scoped to the narrowest set that still works, and are per
project, so a leaked or over-broad credential has a blast radius of one project.

The factory assumes its workloads are untrusted — they are written by agents —
so **rootless Docker** is a named security consideration: one fresh container
per issue, scoped credentials, no root daemon.

## Where the public docs live

This document is the internal design record. The public, published version of
the same design is the Nexus documentation at **https://nexus.nanisoft.com/docs**
(source in the `nexus` repository), which covers the same architecture,
concepts, configuration, operations, and reference material in more depth.
