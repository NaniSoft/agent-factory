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

.NET 10. The solution is in [`src/`](src), and every command is run from the repository
root.

```bash
dotnet build src/agent-factory.slnx    # build the factory and its tests
dotnet test src/agent-factory.slnx     # run the whole suite
dotnet run --project src/agent-factory # run the factory; the board is on port 5000
```

The board is at <http://127.0.0.1:5000>. It is bound to loopback on purpose: the machine
is the trust boundary while the factory runs in development.

`dotnet test` starts the real factory in-process against a temporary `factories/`
directory and a temporary SQLite file, and asserts against what the board renders. There
are no sleeps in the suite and no shared state between tests.

Most of the suite is hermetic and needs neither Docker nor a network: the container
runtime is driven through a recording `IDockerCli` in `Boundary/FakeDockerCli.cs`, which
behaves like the commands it stands in for rather than remembering what it was told. The
two classes that do need a real container are separated and **skipped, with the reason,
on a machine that has no Docker daemon or no worker image** — `DockerFactAttribute` checks
once and says which. Build the image first with
`docker build -t ghcr.io/nanisoft/agent-factory-worker:1 worker/`. `WorkerRoundTests` uses
the factory's own runtime against the real daemon, which is where the container's
properties are read back off the container rather than off the command that made it;
`WorkerImageTests` uses Testcontainers to ask a different question, what is in the image,
without the factory in the way. Neither binds a port, and do not run `dotnet test`
alongside anything else that wants the board's.

## The factory

One process (`src/agent-factory`): the config loader, the work item store, intake, the
loop, and the board, which is an ASP.NET Core endpoint the same process serves. See
ADR-0003.

Three seams are the only things that leave the building; everything else runs for real:
`IClock` in `Clock/`, `INOpenCode` in `Rounds/` (one call is one round), and `IGitHub` in
`GitHub/` (one seam covering both polling and merging — one call is one merge).

`INOpenCode` is implemented: `Rounds/WorkerRoundRunner.cs` runs one round in a real
container, and `Containers/` is the runtime beneath it that owns the container's life.
`IGitHub` still has no implementation, so the process registers a **refusal** in its place
— `GitHub/GitHubNotBuiltYet.cs` — rather than failing to start. A registered component
whose dependency cannot be resolved stops the process starting in development, and the
board is worth having before that adapter exists. The test host's fakes are registered
first and win, so the refusal is never reached under test. It is deleted, not left behind,
when the merger lands (#10).

## The worker container

`Containers/ContainerRuntime.cs` is one worker container from creation to removal, and it
is the whole of the factory's knowledge of Docker. It creates the container, starts it,
follows its log, lifts the round's one result file and its tree out with `docker cp`, and
removes the container — **in a `finally`, not on the success path**. Success, a round that
wrote no result, a container that never started, a caller that cancelled, an exception out
of the log stream: every one of them removes the container. A container that outlives its
round is what makes "nothing survives a round" untrue (ADR-0001) and it leaks the
compensating controls ADR-0012 names, which is why this is tested rather than trusted.

Two details there are load-bearing rather than incidental:

- **The removal addresses the container by name and runs on a token nobody can cancel.** A
  name is known before the container exists, so a `create` whose own output was lost can
  still be undone; and a round is very often torn down *because* its token was cancelled,
  so handing that token to the removal would make the one operation that must happen the
  one operation that could not.
- **The round's token is honoured everywhere else too.** A round that ignored it would keep
  its container for ever, which is the trap `#3` left recorded on `#8`. The loop stops
  waiting on a timed-out round; it does not stop the work, so honouring the token is the
  implementation's job rather than the loop's.

The round command is created with no `-p`, no `-P`, no `-v`, no `--mount`, no `--network`
and no `--privileged`. Those are properties of the *command*, not of the image: an image
that exposes nothing can still be published, and an image with no `VOLUME` can still be
handed one. The round fetches its own repository over the network instead, because the
alternative is a host path inside a box the factory does not trust (ADR-0010, ADR-0012).

`Containers/DockerCli.cs` is the Docker CLI as a process, with **no container-runtime
package reference at all**. ADR-0010 names `docker cp` and `docker logs` as the two ways
anything crosses a round's boundary, so the CLI's verbs are the mechanism rather than an
implementation detail of one. Arguments are passed through `ArgumentList` and never
through a shell, so a reviewer's words cannot become shell syntax on the way in — the
brief reaches the round as an environment variable, and a test asserts a brief full of
metacharacters arrives intact and unexpanded. Both streams are read as the process writes
them, because a 90-minute round that buffered its log would be a round that said nothing
for 90 minutes. On cancellation the local `docker` process is killed and reaped, so a
cancelled round leaves nothing holding a socket to the daemon.

`Rounds/WorkerRoundRunner.cs` is the seam's implementation and **not the agent**. What runs
in the container today is a plain command: the round's brief written outside the working
tree, and the tree it was given read back through the image's own recording wrapper.
Driving the OpenCode CLI against a provider is `#9`. The payload it produces is
correspondingly thin — the result file's own header line, read verbatim, plus the agent's
one optional note — because deriving a result from the records is ADR-0011's work and
belongs to the deriver that has not been written. A round whose own command failed is
still `Produced`: the container worked, and the failure is data inside the result. A round
whose container broke, or that wrote no result, is `Failed`. Telling those apart for
retry purposes is `#7`'s, not this component's.

**There is still no production heartbeat.** Nothing steps the orchestrator or the poller on
a schedule, so in production a work item in Backlog still moves only when a reviewer's
click asks the loop for a step. That gap is unchanged by this ticket and is recorded here
rather than left ambiguous: a real `INOpenCode` exists now, but the driver that steps the
machine between decisions is still nobody's, and adding one is a deliberate call rather
than a side effect. Nothing in the container runtime sleeps, defers or times out; the one
thing it waits for is a container, and that wait is ended by the round's token rather than
by a clock of its own.

## The round's files

`FactoryOptions.RoundsDirectory` is where a round's lifted-out result file and tree land,
one directory per work item, beside the store. It is **kept** after the round: the host
has to be able to reach the round's commit once the container that made it is gone, because
the host pushes and not the container (ADR-0006). Turning that tree into a pull request is
the merger's ticket (`#10`).

## Intake

`Polling/Poller.cs` is intake: it reads the open issues of every project the factory
serves and turns each into a work item in Backlog, one project's turn at a time.

The poller is deliberately **indiscriminate** — no label filter, no assignee filter, no
state filter beyond open, and no gate. That is ADR-0007 and it is not an oversight: a
filter would put factory policy into project configuration, and which issues are worth
building is a judgement a human makes **on the board**, the only surface the design
trusts for human input. The labels and assignees are carried on `OpenIssue` and never
read, and a test says so, so the rule cannot be quietly reintroduced.

Like the loop it is stepped, not timer-driven. One `StepAsync()` is one project's turn,
taken only once `FactoryConstants.PollInterval` has passed since the last pass began — a
comparison against `IClock`, not a `Task.Delay`, so no test sleeps. `PassAsync()` takes
every project's turn in turn. The rotation order is the directory sorted by file name, so
it is deterministic without anyone maintaining it, and it is a project's turn that is
bounded rather than a pass: a busy repository cannot spend a pass on itself.

Intake is idempotent against the store's unique index on `(repo_url, issue_number)`, and
a re-poll of an open issue leaves the work item it finds exactly as it is — same lane,
same base, same rounds. One repository erroring is contained to its own turn; the rest
of the pass still runs, and the next pass tries it again. Nothing polls yet, because
there is no GitHub behind the seam to poll.

## The loop

`Loop/Orchestrator.cs` is the factory's state machine, hand-rolled and deterministic. It
moves work items Backlog → Frontier → In Progress → Review → Done, and it is the whole of
the factory's policy. It references no container runtime: asking `INOpenCode` for a round
and receiving a result is all it knows, so the agent is faked in tests and no Docker
appears.

One `StepAsync()` applies at most one transition, which is what makes the
90-minute `FactoryConstants.RoundTimeout` a comparison against `IClock` rather than a
timer. Like the poller it is stepped rather than driven, and like the poller nothing
drives it on a schedule. There is now a real `INOpenCode` behind the seam, so a step can
start a round for real — but nothing in this process steps it on a timer, and that is
still deliberate rather than an oversight: a background loop that started rounds with no
budget, no concurrency limit and no log would be a factory spending containers on every
issue of every project at once, and the container budget and cross-project concurrency are
`#12`'s. A reviewer's click is the only thing that drives the machine today, and whoever
adds the driver that steps it between decisions owns that decision deliberately.

A step **does** await the merge an approve asks for, because merging is the transition
that approve *is* rather than something asked for earlier and collected later: there is no
heartbeat yet, and a reviewer's click has to do something now. Nothing sleeps, defers or
times out in the loop. A merge that hangs rather than fails is bounded by the seam's own
client when it exists (#10), not by anything here.

## Every way a loop ends

Four endings, three lanes, and one threshold. The loop's step order **is** the asymmetry:
a decision a reviewer made is applied before anything else, then a work item nobody
reviewed, then the pipeline. Presence beats absence.

**The ceiling is 3 rounds, and the third request for changes escalates.** The comparison
is `RoundCount >= FactoryConstants.RoundCeiling` where `RoundCount` is rounds **run**, not
rounds charged — so a work item sitting in Frontier has not yet spent the round it is
about to start, and the board's "round 2 of 3" is honest. This is the off-by-one the
whole ticket turns on: `>` instead of `>=` bites a round early, and the suite catches it.
An exhausted work item is **parked, never merged**, and the loop has no transition out of
a parked one, so a fourth round is not merely refused — it is unreachable.

**A work item in Review past 48 hours is merged.** `FactoryConstants.FeedbackThreshold`
is a single named code constant, not configuration, and the board renders it in a header
and on every Review card's own "auto-merges at" line. The wait is measured from
`WorkItem.ReviewStartedUtc` — when the work item *entered* Review, set by the same write
that moved it there and cleared when it leaves. It is deliberately not `UpdatedUtc`:
anything else that writes to a work item would silently restart the reviewer's clock, and
a work item sent back and reviewed again must get a full threshold rather than the
remainder of the last one. The comparison is against `IClock`, so the suite has no sleeps
and a restart does not forget the wait.

**That merge is a merge.** It goes through the same `IGitHub.MergeAsync` an approve does
and obeys the same rule, so there is no second path to `Done`: with no merger behind the
seam, an ignored work item cannot complete either, and it does not report a change shipped
that was not. It is exactly one attempt, because the work item leaves Review the moment
it lands or parks.

**Escalation parks; rejection is final.** `Swimlanes.Decidable` is the two lanes a
reviewer can still act on — `Review` and `Escalated` — and `Decisions.OfferedIn` is the
set the board renders, which is deliberately the set the store keeps: a parked work item
offers **approve and reject only**, and requesting changes on one is refused with a
message saying why. A button the board offered and the store refused would be a promise
the factory does not keep, and a post by hand that reached a decision the board did not
offer would be a way around the loop's policy. Rejected is absent from that set and
cannot be added, which is what makes a decline final without a later caller remembering.

**A failed merge parks the work item in Escalated.** This is the judgement this ticket
owns, and #20 left it in Review on purpose so the policy would be decided once. The
reasoning is in `Orchestrator.Approve`: a factory that cannot ship what a human approved
has failed, and parking is what takes it out of the threshold's reach — so an unattended
merge attempt every 48 hours for a merge already known to fail cannot happen. That is the
decisive argument; the lane name follows from it. Nothing is lost by parking, because a
parked work item is still mergeable in one click from the board.

**Causes are derived, not stored.** `Pages/HowItEnded.cs` reads the decisions and rounds a
work item already has and says which ending it is, so "escalated" and "rejected" render as
distinct states with distinct causes rather than two lanes whose cards read alike. A
separate cause column would be a second copy of what the record already says, free to
disagree with it. The merge failure's own message is *not* persisted — that is the log
and the response the reviewer was holding.

Still not here, and deliberately: retry classification, backoff, and transient-versus-
permanent (`#7`) — a failed merge is not retried by anything, on any schedule, ever; and
the agent and the result deriver (`#9`), the merger (`#10`), the diff (`#11`), the
container budget (`#12`). Nothing sleeps, defers or times out in any of this: the ceiling
is a count and the threshold is a comparison against `IClock`.

## Done means merged

**A work item is Done only when a merge actually landed.** `DESIGN.md`'s swimlane table
says Done is "approved and merged" and that approve "triggers the auto-merge: the factory
opens the pull request and merges it", so an approve is **not complete until a merge
lands**. `Decision.Approve` used to map straight to `Swimlane.Done` with no merge call
anywhere in the application, which meant the board reported a merge that did not happen —
the exact failure this system exists to prevent.

So the loop applies an approve by asking the one `IGitHub` seam to merge, and only a
merge that came back moves the work item to Done. The merge is **one** call
(`IGitHub.MergeAsync`) and the loop passes only what it knows: the repository and the issue
the change answers. Which pull request the change ships as, what it is called, and how the
branch is pushed are the merger's business behind the seam (#10). The loop has no concept
of a pull request, the way it has no concept of a container.

**Today no merger exists, so an approve cannot complete.** The seam refuses, the reviewer's
decision is recorded and not lost, and the work item is parked. That is the honest
outcome rather than a failure to handle.

**A failed merge leaves the work item in Escalated**, with its approval on the record and
the loop's answer to that approval recorded as Escalated — the reviewer approved, it was
not merged, and the lane says a human has to finish it. #6 owns that decision and the
argument for it is under "Every way a loop ends" above. Three things follow:

- **The loop does not retry it by itself.** Whether a merge that failed is worth another
  attempt, how many, how soon, and telling a transient failure from a permanent one, is the
  retry ticket's (#7). An unclassified, unattended, unbounded retry would be worse than the
  single attempt a reviewer can see and make again. So the decision is recorded *applied*,
  not pending: a pending decision is retried on every step and across every restart.
  Because Escalated is decidable, the board still offers the reviewer the decision form,
  and approving again is a second, complete, human-attributable decision rather than a
  silent re-run. `ApproveTests` asserts exactly this, including across a restart.
- **It leaves the timeout's reach**, which is why the lane is Escalated and not Review. A
  merge that did not land would otherwise still be in Review 48 hours later, and the
  feedback threshold would try again — unattended, for ever, for a merge already known to
  fail.
- **It is not silent.** The loop logs a warning and returns a refusal through
  `StepResult`, and `Pages/Index.cshtml.cs` renders it on the response the reviewer is
  holding — a refusal on the next board read would be lost. The card also says plainly
  which swimlane each decision was applied to, so an approval that did not merge stays
  readable after the page is read again.

One work item's failed merge is contained to that work item: the pipeline behind it keeps
moving.

## The three decisions

`WorkItems/Decision.cs` is the whole of what a work item leaves Review by: approve,
request changes, reject. There is no fourth, and a value the board cannot read as one of
the three is refused rather than guessed at. `Pages/Index.cshtml` renders them as one
form wherever a reviewer can still act — Review offers all three, a parked work item
offers two — and that form is the factory's only write path: the process serves one
route, the board has one reading handler and one writing handler, and a test says all of
it.

The board does not move work items, and it does not merge anything. It records what the
reviewer decided, in their own words, and asks the loop for one step; which swimlane a
decision means is the loop's policy (ADR-0005), so a page that decided lanes itself would
be a second state machine that could disagree with the loop about the same work item. That
one step is also the only thing that drives the loop in production today, and it is
deliberate: a reviewer's click has to do something now, and nothing else is there to step
the machine between decisions until the real agent's heartbeat lands.

What the loop has to **say** also comes back through that step, as `StepResult`, and the
board renders a refusal where the reviewer is looking rather than working out for itself
that a decision failed — which would be it deciding policy the loop owns. Approve is the
case that needs it: Done means merged, and a merge that did not land is not something the
page could work out on its own.

A decision record carries the swimlane it was applied to, **including the swimlane it was
not moved to** — an approval applied to Escalated is a real, readable outcome, not an
absence. What has been acted on is therefore read off the record rather than off the work
item's swimlane, which is what makes applying a decision survive a restart and exactly
once — the swimlane cannot say so, because a request for changes comes back round and the
work item is in Review again with the same decision still on it.

**Feedback** is the reviewer's reasons, kept whole, and the most recent request for
changes on a work item is the brief its next round is handed. A request for changes with
nothing to say is **refused**: the brief is the point of the decision, and a placeholder
in the reviewer's mouth is worse than a refusal. Approve and Reject need no words,
because neither of them briefs a round. The store refuses a decision about a work item
outside `Swimlanes.Decidable` — Review and Escalated — which is what keeps Rejected final
without a later caller having to remember, and refuses one specifically for a parked work
item that asks for changes, because a parked work item is finished by a human rather than
sent round again.

## Project files

A project is one file in [`factories/`](factories), and the served set is exactly the
files there — there is no registry to keep in step with the directory. The schema is
exactly these six values, and a field beyond them is **refused**, not ignored:

| Field | |
| --- | --- |
| `name` | the project's name, unique across the directory |
| `repo.url` | absolute http or https URL |
| `worker.image` | the image each round's worker container starts from |
| `llm.provider` | the LLM provider the agent runs on |
| `keys.github` | the **name** of an environment variable, never its value |
| `keys.llm` | the **name** of an environment variable, never its value |

A file that fails validation keeps its own project out of the rotation and is reported on
the board under "Project files refused"; the factory still starts. Shared, partial,
included and generated project files are all refused: a project is always exactly one
complete file that someone wrote.

Adding a project needs no code change and no rebuild, except that configuration loads at
start, so a changed file means a restart.

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
