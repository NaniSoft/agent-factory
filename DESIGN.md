# Agent Factory Design

The design of the agent factory: a generic system that receives GitHub issues as
use cases, builds the change that answers them by driving OpenCode, and merges
the result back to the repository. One issue in, one reviewed pull request out.

**Status: implemented and running.** This design is the specification the factory
is built against, and the decisions it defers on are recorded as ADRs in
`docs/adr/`. Everything in the sections below exists in code and has been run end
to end against a real repository: an issue was polled, built by an agent in a
fresh working tree, reviewed on the board, and shipped as a merged pull request.
What is *not* claimed here is production readiness — the board is loopback-only,
the deployment is one operator's machine, and auto-merge defaults to off. The
polished, public presentation of this same design is published as the Nexus
documentation and is frozen until reconciliation; see
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
| Round runner + orchestrator | Accepts work items, starts rounds on this machine, drives the build, tracks rounds. |
| Kanban board | Serves the board at port 5000. It holds no board state of its own. |
| Merger | Opens and merges the pull request from the host when a work item is approved. |

These are five components in one .NET process, not five services: the board is an
ASP.NET Core endpoint on port 5000 served by that same process, and the
components call each other as ordinary method calls. The board reads
the records the orchestrator writes. There is no board-side copy of the truth,
and in one process that is the absence of a second copy rather than a
consistency property to maintain.

The orchestrator is a hand-rolled deterministic state machine. It runs no
Microsoft Agent Framework and hosts no agents of its own — the agent a round
drives does all the agentic work. The orchestrator does not start a
process itself: it hands a work item to NOpenCode, which drives OpenCode in a
fresh working tree and returns structured results. NOpenCode is our own
interface, not a package of that name. Keeping that boundary is what
allows the orchestrator's policy to be tested against a fake NOpenCode.

**How those five are laid out in code is not described here.**
[`docs/architecture.md`](docs/architecture.md) is the architecture of record: the
four projects, the dependency rule, the ports and what each is for. This document
describes what the factory *does*, and the earlier version of this section tried to
do both — which is how a design document ends up describing a container runtime
that no longer exists.

## Configuration

The system is configured with a YAML file per project, held in the `factories/`
directory — one file per project, nothing else. Adding a project to the factory
is adding a file to that directory: no component changes, no rebuild.

The schema is **fixed and exactly five values**: `name`, `repo.url`, `llm.model`,
`keys.github` and `keys.llm`. A field beyond those is refused rather than
ignored, so no factory policy can move into project configuration. The model is a
full `provider/model` reference, because the provider prefix is how the factory
knows which canonical environment variable to re-emit the key under.

- **Git repository URL** — where issues are polled from and pull requests opened.
- **API keys** — the credentials the project's builds need, referenced by name.
- **LLM model** — the full `provider/model` reference this project's agents run on.

All of them are per project. The factory's own behaviour — round ceiling,
timeouts, merge policy, retry strategy, round concurrency — is not configurable
per project; it is the factory's, identical everywhere. The single exception is
auto-merge, which is a factory-level setting with a default of **off**, so
silence never ships a change until an operator deliberately turns it on.

The config loader validates a project file before anything downstream sees it.
A file that fails validation keeps its project out of the rotation rather than
failing the whole factory later, mid-run, and the refusal is rendered on the
board. Shared, partial, included and generated files are refused too: a project
is always exactly one complete file someone wrote. Configuration loads once at
start — a changed file means a restart.

## From GitHub issue to work item

The issue poller reads open issues from the configured repository. Every open
issue is a candidate work item — there is no label or assignee filter, and the
board is where a human decides what is worth building. Each issue
becomes a work item with a title, a description, and an initial **Backlog**
status. Work items are persisted in SQLite, the factory's store of record, so the
board and the orchestrator read the same record rather than each keeping its own
state.

Acceptance from Backlog into Frontier is a human act: a reviewer presses a button
on the card, and the loop applies it. Nothing is built until they do.

## Routing: what kind of work is this

A work item does not go from Backlog straight into a build. Before it can be
accepted it costs one **routing round**: an agent that reads the issue's title
and body and returns one of three verdicts.

| Verdict | Means | Then |
| --- | --- | --- |
| `Build` | the issue is already a build ticket | stays in Backlog, waiting for a reviewer's acceptance |
| `Grill` | buildable, but branches of the design are undecided | a human is asked, in the issue tracker |
| `Wayfinder` | the way to the destination is not visible | charted as a map of decision tickets |

The routing round does **not** fetch the repository. The round runner fetches the
tree only when the round's purpose says to, so the saving is the whole of why a
routing round is cheaper than a build round — the costs are bounded by what the
factory chooses to hand over.

What the round writes is a proposal, and the host decides what becomes real. The
round writes `routing.json` outside the working tree; the host reads it after the
round is over, and files the questions or the map as real issues, linked to the
maintainer's issue with GitHub's own sub-issue and blocking relationships. **The
questions are filed before the verdict is recorded**, because a work item waiting
on a question that was never asked anywhere is a card that says the factory needs
something and gives nobody anywhere to give it.

A routing round is not charged to the round ceiling: it is inside the same
concurrency bound as everything else, but reading an issue is not an attempt to
build anything. Build rounds are started ahead of routing rounds, so a reviewer's
acceptance is never made to wait behind the factory's own guess about an issue.

A verdict the factory cannot read — malformed, truncated, a shape this build does
not know — is **parked with the reason on the card**, never guessed and never
defaulted to `Build`. Readiness is a column the factory writes, derived from the
issue; the factory still reads no labels.

`Waiting` is a swimlane of its own for work items parked on a person rather than
on the machine: a map and a question set are not diffs, so the three review
decisions are not offered on one.

## The round

Every round gets a fresh working tree, cloned at the work item's base into the
round's own landing directory. One exists for exactly one round, and nothing
survives it — no cache, no tree, no state from a previous run or from another
project's run. The process's lifetime and the round boundary are the same thing.

In that tree, OpenCode builds and tests the change from the issue, driven
non-interactively as this machine's own child process. The project's own tests
run in the same tree the build produced. Files changed, commands run, and test
outcomes are derived by the factory observing the round rather than from the
agent's own account of them, and are persisted with the work item, so
the reviewer sees what happened rather than a summary of it.

## The Kanban board and the feedback loop

The board is served at **port 5000** with auto-refresh, and it is the only
surface where work is approved. It has five swimlanes:

**Backlog** → **Frontier** → **In Progress** → **Review** → **Done**

A work item in Review offers three decisions:

- **Approve** — triggers the auto-merge: the factory opens the pull request and
  merges it.
- **Request changes** — the work item returns for another round with the
  feedback attached, up to a maximum of **3 rounds** per work item.
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
for rounds. When the third round comes back with feedback still outstanding,
the work item escalates. It is never merged on exhaustion.

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
fresh working tree regardless of project, so concurrency is bounded by the round
concurrency rather than a shared tree or a shared lock. The bound is **2
rounds**, a code constant rather than configuration; within it, the next round
goes to whichever project is holding fewer, so a project with a thousand open
issues cannot take the whole machine from one with one. The board can group or
filter work items by project.

Nothing waits on a timer to be *asked*: the loop is stepped, intake is stepped,
and a heartbeat is the one thing that steps both on a schedule — unless a
Temporal endpoint is configured, in which case one durable workflow per ticket
drives the rounds from the workflow's own history.

## Observability and error handling

- **Structured logging** across all components — the poller, the round runner,
  the board, and the merger — so "what happened to this work item" is answerable
  without reading prose, and concurrent runs do not blur together. Every record
  written within a work item's scope carries the work item, its project, its
  issue and its round.
- **Metrics** for issues processed, builds succeeded and failed, and feedback
  rounds per work item, published on the platform's metrics API with no exporter:
  this process publishes no port, so *where* the numbers go is the deployment's
  business.
- **Retry strategy** for transient failures only, such as GitHub API calls and
  agents that were refused before they began. A build that fails its tests is not
  a transient failure, and retrying it is an expensive way to hide the result. A
  round that did **not run to completion** — a rate limit, a refused provider —
  is transient and is asked for again as the same round; a round's own exit code
  and a command's exit code are two different numbers and are never conflated.
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
(`IGitHub`) substituted and everything else real. Through it, with no network and
no agent, the configuration loader, work item creation and idempotence, the whole
state machine including the exhaustion path, the round ceiling, the 48-hour
merge, escalation and parking, rejection, retry classification, round-robin
across projects, the round concurrency, the heartbeat, persistence to SQLite and
the board's own HTTP surface are all exercised for real.

**Adapters** are tested against the thing they adapt, because that is the only
thing a faked seam cannot prove:

- The **round runner** is tested against real git: the tree a round leaves is a
  genuine repository built by the runner's own clone, changed by a faked agent,
  observed by the real collector, the real deriver and the host's own diff
  reader. What is not tested here is a live model — a model is a provider's
  behaviour, not this factory's.
- The **GitHub adapter** runs against the real API's *shape*, faked at the
  transport so the client, its classification and its git invocations all run
  for real and only the network is gone.
- The **merger** runs against a real bare repository on the machine, so a push
  that half-succeeded is a property of a remote rather than of a stub.

**What the seams cannot see** is contained deliberately, and the known cost is
stated rather than assumed: a single high seam with everything faked can pass
while an adapter is quietly broken. Two requirements of the *services* rather
than of the code are therefore reproduced rather than recorded — GitHub refuses a
request with no `User-Agent` outright, so the faked transport refuses it too; and
a CRLF line-ending setting on the host is asserted against rather than assumed
absent.

Note that **unit and end-to-end are the same tests here** for the policy: the
seam is end-to-end by construction, so there is no four-layer pyramid to keep in
step. The suite's actual layers, and the honest state of each — including one
that was promised by the restructure and has not been written — are in
[`AGENTS.md`](AGENTS.md).

## Deployment

The factory runs on the machine — rounds are that machine's own child processes,
so the factory itself has no container and needs no daemon. One deployment serves
every configured project; there is no per-project deployment.

Two ways to run it. `dotnet run --project src/AgentFactory.Web` runs the process
alone. `dotnet run --project apphost/AgentFactory.AppHost` hosts it under an
Aspire AppHost, which owns the process's lifetime, starts the Temporal server as
a resource beside it, and carries the dashboard and the telemetry export. With no
Temporal endpoint configured the factory runs in store-only mode without the
server; `docker compose up temporal` is the third way to provide one.

The board is bound to loopback and the AppHost does not proxy it: loopback is what
authorises the board, so a proxy hop would sit outside that argument rather than
inside it. The AppHost's dashboard and the OTLP exporter do open sockets, which is
a property this system deliberately gained — see
[`docs/architecture.md`](docs/architecture.md).

The store of record keeps each round's tree beside it: the host pushes from that
tree, so it is kept after the round that made it is gone. Credential values live
one file per credential, read at use time so rotation needs no restart.

`docker-compose.yml` names the machine-only limit in the file itself rather than
leaving it implied — every mapping is `127.0.0.1` with no login because there is
no second machine to ask who you are.

## Security and secrets

Secrets are carried as **environment variables**, and project configuration
references them by name rather than containing them, which keeps
`factories/*.yaml` committable and makes rotation an environment change.
Credentials are scoped to the narrowest set that still works, and are per
project, so a leaked or over-broad credential has a blast radius of one project.

The factory assumes its workloads are untrusted — they are written by agents —
so **the machine is the trust boundary** is the named consideration: the board is
loopback-only, rounds hold scoped credentials, and nothing outside a round that
the machine's own user could not already reach is placed in a round's way. A
round is this machine's own child process, in a fresh working tree the runner
clones for itself.

Two consequences are properties of the design rather than habits. **A round holds
no credential that can write to a remote**: the GitHub key is read on the host,
after the round is over, so a confused or injected agent has nothing that can
push anywhere — and the LLM key does go in, because the agent cannot reach a
provider without it, scoped to one project and dying with one process. And
**nothing crosses a round's boundary but files and the exit code**: the tree is
cloned by the round itself and the result leaves as one file the factory wrote
beside it.

Because a project file in `factories/` is *served* rather than sampled, this
repository commits none. See `factories/README.md`.

## Where the public docs live

This document is the internal design record, and it is the specification the
factory is built against. The public, published version of the same design is
the Nexus documentation at **https://nexus.nanisoft.com/docs** (source in the
`nexus` repository), which covers the same architecture, concepts, configuration,
operations, and reference material in more depth. It is frozen while the
implementation runs and is reconciled in one sweep at the first working
milestone.
