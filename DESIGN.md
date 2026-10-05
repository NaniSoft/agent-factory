# Agent Factory Design

The design of the agent factory: a generic system that receives GitHub issues as
use cases, builds the change that answers them using code-server and OpenCode,
and merges the result back to the repository. One issue in, one reviewed pull
request out.

**Status: implemented and running.** This design is the specification the factory
is built against, and the decisions it defers on are recorded as ADRs in
`docs/adr/`. Everything in the sections below exists in code and has been run end
to end against a real repository: an issue was polled, built by an agent in a
fresh worker container, reviewed on the board, and shipped as a merged pull
request. What is *not* claimed here is production readiness — the board is
loopback-only, the deployment is one operator's machine, and auto-merge defaults
to off. The polished, public presentation of this same design is published as the
Nexus documentation and is frozen until reconciliation (ADR-0002); see
[Where the public docs live](#where-the-public-docs-live).

Where this document and the code can be read for the current truth, the code is
the implementation and this is the design; [`AGENTS.md`](AGENTS.md) records what
was built and how it is tested.

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

The schema is **fixed and exactly six values**: `name`, `repo.url`, `worker.image`,
`llm.model`, `keys.github` and `keys.llm`. A field beyond those is refused rather
than ignored, so no factory policy can move into project configuration. The model
is a full `provider/model` reference, because the provider prefix is how the
factory knows which canonical environment variable to re-emit the key under.

- **Git repository URL** — where issues are polled from and pull requests opened.
- **API keys** — the credentials the project's builds need, referenced by name.
- **LLM model** — the full `provider/model` reference this project's agents run on.
- **Worker Docker image** — the image each round's worker container starts from.

All of them are per project. The factory's own behaviour — round ceiling,
timeouts, merge policy, retry strategy — is not configurable per project; it is
the factory's, identical everywhere. The single exception is auto-merge, which is
a factory-level setting with a default of **off**, so silence never ships a change
until an operator deliberately turns it on.

The config loader validates a project file before anything downstream sees it.
A file that fails validation keeps its project out of the rotation rather than
failing the whole factory later, mid-run, in a worker container, and the refusal
is rendered on the board. Shared, partial, included and generated files are
refused too: a project is always exactly one complete file someone wrote.
Configuration loads once at start — a changed file means a restart.

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

**Feedback timeout triggers auto-merge** after a threshold of **48 hours**: a work
item left in Review past that point is merged rather than holding the pipeline.
This is the design's deliberate answer to a reviewer who is not watching, and it is
gated by a factory-level setting whose **default is off** — so out of the box a
work item waits in Review until a human decides, and turning the threshold on is
a deliberate edit. The threshold and the live mode are both rendered on the board,
per work item, because a reviewer must never have to wonder whether their silence
can ship a change. The merge is always a real, revertible pull request.

**Done means merged.** An approval is not complete until a merge landed; a merge
the client could not carry out leaves the work item in Escalated with its
approval on the record, rather than reporting a merge that did not happen.

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
repository cannot starve the rest. The rotation order is derived from the
directory by file name rather than maintained by hand, so it is deterministic
without upkeep.

Rounds for different projects do not block each other: a long build in one
project does not stop another's issue from starting. Each round still gets a
fresh worker container regardless of project, so concurrency is bounded by the
container budget rather than a shared working tree or a shared lock. The budget is
**2 worker containers**, a code constant rather than configuration; within it,
the next container goes to whichever project is holding fewer, so a project with
a thousand open issues cannot take the whole machine from one with one. The board
can group or filter work items by project, and renders the budget in use — a
bounded machine that says nothing about its bound is indistinguishable from a
wedged one.

Nothing waits on a timer to be *asked*: the loop is stepped, intake is stepped,
and a heartbeat is the one thing that steps both on a schedule.

## Observability and error handling

- **Structured logging** across all components — the poller, the worker, the
  board, and the merger — so "what happened to this work item" is answerable
  without reading prose, and concurrent runs do not blur together. Every record
  written within a work item's scope carries the work item, its project, its
  issue and its round.
- **Metrics** for issues processed, builds succeeded and failed, and feedback
  rounds per work item, published on the platform's metrics API with no exporter:
  this process publishes no port, so *where* the numbers go is the deployment's
  business.
- **Retry strategy** for transient failures only, such as GitHub API calls and
  container starts. A build that fails its tests is not a transient failure, and
  retrying it is an expensive way to hide the result. A round that did **not run
  to completion** — a rate limit, a refused provider — is transient and is asked
  for again as the same round; a round's own exit code and a command's exit code
  are two different numbers and are never conflated.
- **Escalation** for work items that fail permanently or exhaust their rounds.
  Escalated work items are visible on the board, alongside failed builds and
  round counts. A merge that could not land parks its work item rather than
  leaving it to retry unattended.

Nothing in the failure path ends in silence: every outcome is a state the board
renders. Intake reports its own state on the board in three distinguishable
forms — never polled, polled, failing — because an empty lane under a broken
factory must not read as a project with nothing to do.

## Testing strategy

The real split is **policy versus adapter**, and it is the one that matters rather
than a count of folders.

**Policy** is tested through one application seam: the real factory is started
in-process, with `IClock`, the agent seam (`INOpenCode`) and the GitHub seam
(`IGitHub`) substituted and everything else real. Through it, with no container
and no network, the configuration loader, work item creation and idempotence,
the whole state machine including the exhaustion path, the round ceiling, the
48-hour merge, escalation and parking, rejection, retry classification,
round-robin across projects, the container budget, the heartbeat, persistence to
SQLite and the board's own HTTP surface are all exercised for real.

**Adapters** are tested against the thing they adapt, because that is the only
thing a faked seam cannot prove:

- The **worker adapter** runs a real container against the real image — an image
  that starts, a toolchain that is actually present, a round that really runs,
  a result file that really comes back — with a deterministic script standing in
  for the agent, because a model is not what is under test. Separated and skipped
  with the reason on a machine with no daemon.
- The **GitHub adapter** runs against the real API's *shape*, faked at the
  transport so the client, its classification and its git invocations all run
  for real and only the network is gone.
- The **merger** runs against a real bare repository on the machine, so a push
  that half-succeeded is a property of a remote rather than of a stub.

**What the seams cannot see** is contained deliberately, and the known cost is
stated rather than assumed: a single high seam with everything faked can pass
while an adapter is quietly broken. Three requirements of the *services* rather
than of the code are therefore reproduced rather than recorded — GitHub refuses a
request with no `User-Agent` outright, so the faked transport refuses it too;
`docker cp` copies a directory *into* an existing destination rather than
replacing it, so the faked CLI does the same; and a CRLF line-ending setting on
the host is asserted against rather than assumed absent.

Note that **unit and end-to-end are the same tests here**: the policy seam is
end-to-end by construction, which is why the earlier four-layer description
(unit / integration / end-to-end / chaos) is superseded by this one rather than
being a second thing to keep in step.

## Deployment

The factory deploys with **Docker Compose**. One application container serves the
board at port 5000 and hosts every component above; the only other service is
the Docker runtime it starts worker containers against (ADR-0003). One
deployment serves every configured project; there is no per-project deployment.
Worker containers are not long-running members of the deployment — they start
on demand and are removed when the round ends. Review workspaces are a second
kind of container, spawned on demand from a work item's card and destroyed on a
decision; they are the only thing this deployment publishes a port for, and it
binds to loopback.

`docker-compose.yml` names the machine-only limit and the rootful-daemon
deviation in the file itself rather than leaving either implied — the board is
mapped to `127.0.0.1` with no login because there is no second machine to ask
who you are, and the mounted Docker socket is the bounded ADR-0012 deviation that
Docker Desktop forces, compensated by the controls the worker image enforces.

Three volumes are kept: the project files, the credential values (one file per
credential, read at use time so rotation needs no restart), and the store of
record with each round's lifted tree beside it.

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

Two consequences are properties of the design rather than habits. **A round holds
no credential that can write to a remote**: the GitHub key is read on the host,
after the container is gone, so a confused or injected agent has nothing that can
push anywhere — and the LLM key does go in, because the agent cannot reach a
provider without it, scoped to one project and dying with one round. And **a round
is given no host path and publishes no port**: the tree is fetched over the
network and the result leaves as one file the host lifts with `docker cp`
(ADR-0010).

Because a project file in `factories/` is *served* rather than sampled, this
repository commits none — the examples live in `worker/examples/`, outside the
directory the loader reads. See `factories/README.md`.

## Where the public docs live

This document is the internal design record, and it is the specification the
factory is built against. The public, published version of the same design is
the Nexus documentation at **https://nexus.nanisoft.com/docs** (source in the
`nexus` repository), which covers the same architecture, concepts, configuration,
operations, and reference material in more depth. It is frozen while the
implementation runs and is reconciled in one sweep at the first working
milestone (ADR-0002).
