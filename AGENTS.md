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

## The factory

One process (`src/agent-factory`): the config loader, the work item store, intake, the
loop, and the board, which is an ASP.NET Core endpoint the same process serves. See
ADR-0003.

Three seams are the only things that leave the building; everything else runs for real:
`IClock` in `Clock/`, `INOpenCode` in `Rounds/` (one call is one round), and `IGitHub` in
`GitHub/` (one seam covering both polling and merging — one call is one merge).

`INOpenCode` and `IGitHub` have no implementation yet, so the process registers a
**refusal** in their place — `Rounds/NOpenCodeNotBuiltYet.cs` and
`GitHub/GitHubNotBuiltYet.cs` — rather than failing to start. A registered component
whose dependency cannot be resolved stops the process starting in development, and the
board is worth having before the adapters exist. The test host's fakes are registered
first and win, so neither refusal is ever reached under test. Both are deleted, not left
behind, when the real adapters land (#8 and #10).

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
drives it on a schedule: there is no real `INOpenCode` to step against, and a background
loop that could never run a round is noise. Whoever adds the real agent owns the
heartbeat that steps both.

A step **does** await the merge an approve asks for, because merging is the transition
that approve *is* rather than something asked for earlier and collected later: there is no
heartbeat yet, and a reviewer's click has to do something now. Nothing sleeps, defers or
times out in the loop. A merge that hangs rather than fails is bounded by the seam's own
client when it exists (#10), not by anything here.

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
decision is recorded and not lost, and the work item stays in Review. That is the honest
outcome rather than a failure to handle.

**A failed merge leaves the work item in Review**, with its approval on the record and the
loop's answer to that approval recorded as Review — the reviewer approved, and it was not
merged. Three things follow, and the first two are judgement calls:

- **It is not Escalated.** Where a failed merge *goes* — escalated, parked, counted — is
  the exhaustion and escalation ticket's (#6). A lane invented here would be that policy
  decided twice, in the wrong place.
- **The loop does not retry it by itself.** Whether a merge that failed is worth another
  attempt, how many, how soon, and telling a transient failure from a permanent one, is the
  retry ticket's (#7). An unclassified, unattended, unbounded retry would be worse than the
  single attempt a reviewer can see and make again. So the decision is recorded *applied*,
  not pending: a pending decision is retried on every step and across every restart. The
  work item is still in Review, so the board still offers the reviewer the decision form,
  and approving again is a second, complete, human-attributable decision rather than a
  silent re-run. `ApproveTests` asserts exactly this, including across a restart.
- **It is not silent.** The loop logs a warning and returns a refusal through
  `StepResult`, and `Pages/Index.cshtml.cs` renders it on the response the reviewer is
  holding — a refusal on the next board read would be lost. The card also says plainly
  which swimlane each decision was applied to, so an approval that did not merge stays
  readable after the page is read again.

One work item's failed merge is contained to that work item: the pipeline behind it keeps
moving.

**The loop is currently unbounded, and that is a known gap rather than an oversight.** A
reviewer's request for changes puts a work item back in the build, the next round it runs
is another round counted against it, and nothing yet says when to stop. The round
ceiling is the exhaustion and escalation ticket's (#6) and it is deliberately not here:
no ceiling, no fudge factor, no incidental limit. So the round count on the board is
what a work item has spent, **not** a bound, and a work item a reviewer keeps sending
back will keep costing worker containers until that ticket lands.

## The three decisions

`WorkItems/Decision.cs` is the whole of what a work item leaves Review by: approve,
request changes, reject. There is no fourth, and a value the board cannot read as one of
the three is refused rather than guessed at. `Pages/Index.cshtml` renders the three as
one form, in Review and nowhere else, and that form is the factory's only write path:
the process serves one route, the board has one reading handler and one writing handler,
and a test says all of it.

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
not moved to** — an approval applied to Review is a real, readable outcome, not an absence.
What has been acted on is therefore read off the record rather than off the work item's
swimlane, which is what makes applying a decision survive a restart and exactly once — the
swimlane cannot say so, because a request for changes comes back round and the work item is
in Review again with the same decision still on it.

**Feedback** is the reviewer's reasons, kept whole, and the most recent request for
changes on a work item is the brief its next round is handed. A request for changes with
nothing to say is **refused**: the brief is the point of the decision, and a placeholder
in the reviewer's mouth is worse than a refusal. Approve and Reject need no words,
because neither of them briefs a round. The store refuses a decision about a work item
that is not in Review, which is what keeps Rejected final without a later caller having
to remember; an Escalated work item cannot be decided yet, and the parking ticket (#6)
is what widens that.

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
