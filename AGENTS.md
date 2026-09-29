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

**The test host does not start the heartbeat.** `FactoryHost` builds the factory with
`drivingTheMachine: false`, so the driver is registered and resolvable but nothing ticks on
its own; a test calls `TickAsync()` to move the machine the way production does, once, with
no sleep. Without that, a live five-second timer would run alongside every test that
asserts on the state of the machine and the whole suite would be a race against it. This is
the only thing the test host switches off, and it is switched off by an explicit argument
rather than by a setting nobody can find.

Most of the suite is hermetic and needs neither Docker nor a network: the container
runtime is driven through a recording `IDockerCli` in `Boundary/FakeDockerCli.cs`, which
behaves like the commands it stands in for rather than remembering what it was told. It
stands in for `docker cp` with real directories too, not only text files, because a round's
tree is a git repository with a packfile in it and the host's diff reader runs `git` against
whatever that `cp` left behind — a fake that could only produce text would make a diff test
a test of the fake. The one requirement the review tests add is the `git` binary, which any
checkout of this repository already has. The
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

`Rounds/WorkerRoundRunner.cs` is the seam's implementation **and the agent's driver**. The
round's brief — the issue, the project, and the reviewer's own words from the round before
— is composed by `Rounds/RoundBrief.cs`, written outside the working tree, and handed to
the OpenCode CLI as a **file**: the command line is `opencode run --standalone --auto
--file "$out/brief.md" -- "$AGENT_FACTORY_AGENT_PROMPT"`, and both the brief and the prompt
are the factory's own, with the reviewer's words quoted in and never expanded. Three details
there are deliberate:

- **`--auto` is required, not a convenience.** Without it the CLI stops and waits for a
  human to approve each edit, and there is no human inside a worker container. What it
  widens is the agent's freedom *inside* a container that holds no write credential, has no
  host path and publishes no port (ADR-0006, ADR-0010, ADR-0012), so there is nothing
  outside it for a confused or injected agent to reach.
- **`--standalone`** gives the round a private server that dies with the process, rather
  than the CLI's background service. The worker README names that service as a loopback
  listener; this way there is not one.
- **The brief is the issue, not the issue number.** `Round` carries `IssueTitle` and
  `IssueBody` and the orchestrator fills both from the work item. A round handed a number
  has to go and look up what it means, and a brief that does not carry the maintainer's
  words is the factory paraphrasing the work it was asked to do.

The image's recording wrapper under-reports, and that is **known and accepted**:
`worker/README.md`'s "What the wrapper does not see" is accurate, and option 2 there
(pointing the agent's own shell at the wrapper) was tried against OpenCode v2.0.18 and
**does not work** — the CLI's shell tool bypasses `$SHELL`, so a wrapper installed there
records nothing. The brief therefore takes option 1, telling the agent to prefix its shell
work with `run`. That gap is contained the way the image says it is, and the containment is
the reason this design is safe: **ADR-0011's guarantee is carried by `git status` and
`git diff`, not by the command log.** A build tool that bypassed `run` still shows up in the
files changed.

### Credentials: one goes in, one does not

`Credentials/ICredentialReader` is the **only** thing in the process that can turn a
credential *name* into a credential *value*, and `WorkerRoundRunner.EnvironmentFor` is the
**only** caller of it. `PolicyTests` asserts both by IL scan, so ADR-0006 is structural
rather than a matter of care:

- **The LLM key goes in.** The agent cannot reach a provider without it. It is scoped to
  one project, lives for one container, and is never logged. When the environment does not
  have it the round **still runs** and a warning says so — refusing there would be the
  factory deciding for itself that the agent needs one, and a round handed nothing that
  says nothing reads on a board as a round that needed nothing.
- **The GitHub write token never does.** Its name is never passed to the reader, so the
  value is never in the runner's hands and cannot be handed over even by accident.

### The deriver

`Results/RoundResultDeriver.cs` is ADR-0011's whole mechanism. It reads the result file and
turns the records into what a reviewer judges, and it has **no way to ask the agent
anything** — its only dependency is a logger, which `PolicyTests` asserts. `PolicyTests`
also asserts by IL scan that a `ChangedFile` is constructed in exactly two places and a
`CommandOutcome` in exactly one, both inside the deriver's git and record readers, so a
result cannot grow a path that git did not report.

`FilesChanged` is a **union** of `git status` and the diff, and that is load-bearing: a
file the round committed is clean in status and visible in the diff, and an untracked
scratch file is the reverse. Reading either alone silently drops half of what a reviewer
needs, and the untracked one is what an agent leaves by accident.

A result file that is malformed, truncated or absent still produces a `DerivedResult`,
carrying `UnreadableBecause` and nothing invented — an unreadable result reports no files
and no commands rather than reporting none, because "this could not be read" and "this
round changed nothing and ran nothing" are very different claims. Lines the deriver could
not read are **counted** and the count reaches the board, which is the design's own named
failure — "a results payload that parses but is missing the field the reviewer needs" — said
out loud rather than left for a reader to infer from a shorter file.

`Results/ResultPayload.cs` renders that into the text the board shows. Order is a
reviewer's order: the facts first, the agent's note last, under a heading that says nothing
above it is derived from. A result with no files and no commands still renders as a result,
because an empty `<pre>` on a card reads as a round the factory failed to record.

A round whose own commands failed is still `Produced`, and the failing exit codes are data
inside the payload. The deriver has **no field saying which commands were tests** and none
may be added: the repository's own scripts decide what tested means, and a factory that
guessed would be imposing a test convention (ADR-0011).

### The round's log

`RoundResult`, `RoundResultRecord` and the `round_results` table each gained a `log`, and
the board renders it in a `<details>` on the round's own card. It is the **tail**, bounded,
because a ninety-minute build's output does not belong in a database row — the whole log
still went to `ILogger` as the round ran. It exists because #3 left the gap: a round with no
readable result had nowhere to point a reviewer, and now it has the log. `ContainerRuntime`
now keeps its tail whether or not a caller asked for progress; it used to be fed only when
one did, which would have made the tail silently empty for exactly the caller that needs
it.

**The heartbeat landed with the container budget, and nothing in the container runtime
changed.** `Driving/FactoryDriver.cs` is the driver every ticket up to here recorded as
missing — see **The heartbeat** under **The loop** for what it is and why it is safe. The
gap this paragraph used to describe is closed: the agent runs, the result is derived, the
board renders both, and something now steps the machine between decisions.

What did *not* change is the runtime. It still sleeps for nothing, defers for nothing and
times out nothing; the one thing it waits for is a container, and that wait is ended by the
round's token rather than by a clock of its own. That is the property the budget relies on —
a round's container is gone before the next one starts, so the budget is a count of live
containers and not a count of rounds that have ever been asked for.

## Testing the rounds and the results

Two layers, and the split between them is the honest one.

`Results/RoundResultDeriverTests.cs` is **hermetic**: result files are written to a temp
directory in the shape `worker/bin/worker-collect` writes them. It proves the reading — git
porcelain v2, diff headers, quoted paths, renames, exit codes, truncation, degradation —
with no Docker and no model. Its limit is stated in its own doc comment: a fixture the test
wrote agrees with the deriver by construction.

`Containers/AgentRoundTests.cs` is the other half, against a **real container**: the
factory's own `ContainerRuntime`, the real image, the real entrypoint, the real recording
wrapper, the real collector, and the real deriver over whatever actually came back. What
runs in the container is a **deterministic script**, exactly as `worker/smoke-test.sh` does
it and for the same reason — an LLM is not what is under test. It changes a file, runs a
passing command, runs a failing one, leaves a scratch file, writes a note, and commits, so
every case the deriver has to get right is exercised against a real result file.

`AgentFactAttribute` is the credential seam. It probes once whether the OpenCode CLI inside
the image can reach **any** provider, and skips with the reason stated when it cannot — the
same idiom as `DockerFactAttribute`, and the same intent. It does not invent a key, does not
stub a provider, and does not let a test pass on a fabricated round. A machine with no LLM
credential still runs every derivation test and every deterministic container test; it skips
only the one that needs a model.

Note the assertion in the agent test is **deliberately one-directional**: if the file the
brief asked for is on disk, the result must name it (a result that omits a real change is the
failure this ticket exists to prevent); if it is not there, the result must not claim it,
but a round that did not do the work is a legitimate outcome and not a test failure. A model
is a model, and a test that asserts it obeys tests the provider.

### Testing the review surface

`Review/DiffOnTheBoardTests.cs` drives the board over its real HTTP surface, and it is mostly
faked — the agent yields a `HostDiff` a test wrote, and the assertion is about how the card
renders it. One test is not, and it is the one that matters:
`The_diff_on_the_board_is_git_diff_of_the_tree_a_real_round_left` runs the **real**
`WorkerRoundRunner`, the real container runtime, the real deriver, the real diff reader and
the real board, with only `IDockerCli` substituted and only so that `docker cp` hands over a
genuine git repository. `Review/LiftedTree.cs` builds it, and the fake copies the directory
out byte for byte with its **read-only pack files intact** — the shape a tree lifted from a
Linux container has on a Windows host, reproduced rather than assumed, since the reader has
to survive it. A diff asserted against a fixture the test itself wrote would be a fixture
agreeing with itself, and that is the same limit the deriver's own doc comment states.

`FactoryHost.RunTheMachineAsync` exists for that test. `Settle` stops the moment a round is
still running, which is right for a fake that completes inside the call and wrong for a real
round that copies a tree off disk and runs `git` against it; this steps until the machine
holds nothing, yielding between steps, and fails loudly rather than hanging.

**One test does not prove what a reader might assume.** The fold-restore is asserted as the
*shape* it is written against — that every section carries a `data-open-key` naming its work
item, round and path — and **nothing executes the script**. So the suite proves the key is
right and the restore is written against it; it does not prove a browser reopened anything.

## The round's files

`FactoryOptions.RoundsDirectory` is where a round's lifted-out result file and tree land,
one directory per work item, beside the store. It is **kept** after the round: the host
has to be able to reach the round's commit once the container that made it is gone, because
the host pushes and not the container (ADR-0006). Turning that tree into a pull request is
the merger's ticket (`#10`).

It is kept for a second reason now, which is the review surface — see **The diff in
Review** below. The board's diff is `git diff` against that directory, which is why the
directory is not cleaned up after a round.

## The diff in Review

**The review surface's diff is `git diff` run on the host, against the tree the round left.**
That is the whole design of this ticket, and the acceptance criterion is literally "generated
host-side from the retrieved commit". `Results/HostDiffReader.cs` runs the `git` binary as a
process; there is no Git library and no HTTP client anywhere in the path.

It is **not** the diff the container recorded. The deriver already reads a diff out of the
result file, and that one is bounded by the image — on a large change it is a prefix, and a
board built on it would show a reviewer part of a diff that looked like all of it. The host's
is generated from the tree, is not bounded, and exists even for a round that wrote no result
file at all, which is the round a reviewer most wants to see something of. The container's
bounded copy is still recorded (`DerivedResult.DiffTruncated`) and the card **says** when it
was bounded, because a reviewer who notices two observations of one change is owed the answer.

Three properties of the reader are deliberate and each is a refusal rather than a
convenience:

- **`--no-ext-diff` and `--no-textconv`.** The tree is a repository this factory did not
  write, and a `.gitattributes` line or a `diff =` driver in it names an arbitrary program
  for git to run. A reviewer asking what changed must not run it.
- **`safe.directory` through the environment**, exactly as the worker image sets it
  system-wide. The tree was created by uid 1000 inside a Linux container and git refuses to
  read a repository it does not own — which would leave the diff silently *absent* rather
  than wrong. It is injected through the environment so that reading a round's diff never
  modifies a round's tree.
- **Nothing is written to the tree.** A tree lifted out of a Linux container arrives on
  Windows with read-only pack files, and a diff is a read of them. Anything that deleted or
  moved files in there would have to clear the attributes first, and nothing here does.

**The base is the commit the round started from**, as the round's own result file records it,
with the work item's base branch as a fallback for a round whose result could not be read.
That distinction is load-bearing and is what `The_diff_on_the_board_is_git_diff_of_the_tree_a_real_round_left` tests: the fixture moves the base branch on while the round works, so diffing against the branch tip would put a commit the round never saw on the card as if the round had written it.

**The diff is a column on the round, not part of the payload.** `round_results` carries
`diff`, `diff_base`, `diff_tree`, `diff_container_bounded` and `diff_unavailable` — five
plain columns rather than one column of JSON. That is deliberate: the store is the store of
record (ADR-0009), a row it cannot read back is an agent's work nobody can look at, and JSON
in a column has to be deserialised into exactly the "parses but is missing the field the
reviewer needs" failure the design names — imported into the one place that has to be
reliable. Keeping the diff beside the payload also means a **restart cannot lose it**, and
that a board render reads the record rather than re-running git against a directory it might
no longer have.

### How a large diff is presented, and why

`Pages/HowToReadTheDiff.cs` is the whole of the judgement, and the view renders what it says
without deciding anything. Four rules, and the argument for each:

- **One collapsible section per file, with the path, what git says happened, and the line
  counts on the summary line.** A reviewer's first question about a change is its *shape* —
  which files, how big, added or deleted — and that is three facts per file. Reading it from
  a header line means the shape costs one line per file rather than a scroll. The counts are
  arithmetic over the file's own text, not a verdict about it, so a reviewer can check them.
- **The first section open, the rest closed.** One open file means the card shows a change
  rather than a list of file names; closing the rest is what stops twelve files arriving as a
  wall. Every file's text is in the page either way, which is what makes closing one a
  presentation choice rather than a summary.
- **Git's own text underneath, unchanged.** No highlighting, no elision, no collapsing of
  unchanged context lines, no rewriting. A reviewer can hold the card against their own
  `git diff` and get the same bytes, which is the property that makes the card evidence
  rather than a rendering of it. This is why the diff is **modelled** (`Results/DiffDocument.cs`)
  and not re-parsed out of the payload at render time: the model reads git's format through
  `Results/GitDiff.cs`, which is the *one* reader of that format in the process — the deriver
  uses it too, so the two cannot come to disagree about what a section says.
- **A bound, on whole files, that says what it left off.** `MaxFiles` (200) and `MaxLines`
  (20 000) are generous, and the ordinary round is nowhere near them. What matters is the
  shape of the cut: **never mid-hunk**, because half a file looks like all of a file, which
  is the failure this whole rendering exists to prevent. And what is left off is counted in
  files and lines, named, and pointed at — the tree, on this machine, at a path the card
  prints. A diff that stops without saying how much it stopped at is indistinguishable from a
  diff of a smaller change.

**Open sections survive the reload.** The board refreshes itself every five seconds, which is
right for a board and would be wrong for a fold. The key is rendered by the board as one
`data-open-key` attribute — work item, round, path, all three — and kept in `sessionStorage`.
One attribute rather than a key the script assembles, because the board is the only thing
that knows which work item and round a section belongs to, and a key built from the path
alone would open round 2's copy of a file a reviewer opened in round 1.

### What the review surface does not need

**GitHub, and a pull request number.** The diff is a directory on the host and a commit on
it, both knowable before a branch is pushed and long before a pull request exists. This is
also #10's seam note honoured: `MergeAsync(repoUrl, issueNumber, ct)` takes an **issue**
number because the loop has no concept of a pull request, and a review surface that required
one would be the first thing in the factory to have one. `PolicyTests` asserts it
structurally — only `Poller` and `Orchestrator` call `IGitHub` at all, and the diff reader's
constructor takes no client, no agent, no credential reader and no `HttpClient`.

Three further honesty cases the board distinguishes rather than collapsing, each with a test:

| On the card | Means | Not |
| --- | --- | --- |
| `data-state="empty"` | the round changed nothing | a round nobody looked at |
| `data-state="unavailable"` | no diff could be generated, and why | a round that changed nothing |
| no `data-diff` at all | no diff on record — an older row, or a round with no tree | either of the above |

A round recorded **before** the diff column existed is not retrofitted with an empty one: the
tree it would have been read out of was not kept per round, and a fabricated empty diff would
claim a round changed nothing when in fact nobody looked.

## Intake

`Polling/Poller.cs` is intake: it reads the open issues of every project the factory
serves and turns each into a work item in Backlog, one project's turn at a time.

The poller is deliberately **indiscriminate** — no label filter, no assignee filter, no
state filter beyond open, and no gate. That is ADR-0007 and it is not an oversight: a
filter would put factory policy into project configuration, and which issues are worth
building is a judgement a human makes **on the board**, the only surface the design
trusts for human input. The labels and assignees are carried on `OpenIssue` and never
read, and a test says so, so the rule cannot be quietly reintroduced.

Like the loop it is stepped, not timer-driven, and **what steps it is now the heartbeat**
rather than a reviewer's click — see **The heartbeat** under **The loop**. What did *not*
change is the poller itself: it is still strictly serial, one project's turn at a time, and
`PassAsync` still awaits each in turn. That is a decision rather than an oversight, and it is
the one `#4` asked to be settled.

**Intake stays serial deliberately.** Parallelising it would buy a little latency in the
one part of the factory that was never the bottleneck — a pass is already bounded per
project, so a busy repository cannot spend a pass on itself — while costing the one property
that makes the multi-project guarantee provable at all. The rotation order *is* the
anti-starvation mechanism: one project's turn at a time, in a derived order, is what makes
"one noisy repository cannot starve the rest" a fact about the code rather than a hope about
timing. A parallel pass would interleave the reads, and the order `IntakeTests` asserts
against would become a property of scheduling rather than of the directory.

One `StepAsync()` is one project's turn,
taken only once `FactoryConstants.PollInterval` has passed since the last pass began — a
comparison against `IClock`, not a `Task.Delay`, so no test sleeps. `PassAsync()` takes
every project's turn in turn. The rotation order is the directory sorted by file name, so
it is deterministic without anyone maintaining it, and it is a project's turn that is
bounded rather than a pass: a busy repository cannot spend a pass on itself.

A tick from the driver asks for a turn and the poller decides for itself whether one is
due, so the cadence above is the poller's own and the driver's five-second tick never turns
into a read the interval has not asked for. Intake is bounded the other way too: a project
being paced out of a pass is a project's own turn being skipped, and the rotation carries on
to the others, which is what `A_failing_project_still_does_not_stop_the_rest_of_the_rotation`
asserts over a simulated day.

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
timer. A step **does** await the merge an approve asks for, because merging is the transition
that approve *is* rather than something asked for earlier and collected later — a reviewer's
click has to do something now. Nothing sleeps, defers or times out in the loop. A merge that
hangs rather than fails is bounded by the seam's own client when it exists (#10), not by
anything here.

**The step order changed with the budget, and that is the generalisation.** A round that has
come back is landed first, then a decision a reviewer made, then a work item nobody reviewed,
then a parked merge's retry, and only then the pipeline. With one round in flight a running
round was the only thing the machine did, and it blocked everything else — including a
reviewer's decision and a 48-hour merge. It no longer does, because none of those starts a
container and none of them should wait behind a round that has ninety minutes to run. What
waits is only the *starting* of another round, and only when the budget is full.

### The container budget

**Concurrency is bounded by a count of worker containers and by nothing else.**
`FactoryConstants.ContainerBudget` is `2`, sized for this machine's 7.19 GB in which each
worker runs an agent, a toolchain and a build. It is a code constant and not configuration,
like everything else in that class, so no project can have a different budget and no
deployment can quietly raise the machine's ceiling.

It is deliberately **not** a lock, a shared working tree or a per-project quota. Each round
gets its own fresh container whatever project it is for (ADR-0001), so there is nothing
shared to serialise and nothing to take a lock on. The only scarce thing is a container and
the budget is the count of those — `_inFlight` is a `Dictionary<Guid, InFlight>` rather
than the single optional it was, because a count is the only thing that can be bounded, and a
boolean slot would be one round in flight with a different name.

Three things about it are load-bearing, and each is asserted rather than left to be
discovered:

- **A work item waits in Frontier, not in Backlog.** `AcceptIntoFrontier` is no longer gated
  on the budget: Frontier is the waiting room, and what the budget governs is the number of
  rounds *started*. Gating acceptance on the budget would move the wait into Backlog and
  leave the two lanes meaning the same thing, and it would make a work item's place in the
  queue depend on how many containers happened to be free. A reviewer watching a long build
  wants to see the next work item already queued behind it, not still in Backlog as though
  nothing had been accepted.
- **The budget is enforced in exactly one place**, `Orchestrator.ASlotIsFree`, and nothing
  else consults it. `PolicyTests` also asserts the loop is the only caller of
  `INOpenCode.RunRoundAsync`, which is the structural half of the same claim: a second
  component able to start a round would be a second way to spend a container that nothing
  in the loop could see or count.
- **A merge takes no slot at all.** #6 left this noted as a shape to revisit, and the answer
  is that a merge starts no container, runs no agent and costs nothing the budget exists to
  protect. Making it wait for one would let a build in one project decide when a change
  reaches another repository, and a factory with two long builds running would stop shipping
  what a human had already read and approved — a less safe factory than one with no builds
  at all. So the step order carries it, and `MergeWhatNobodyReviewed` reads no in-flight
  state whatever. A reviewer's decision is the same, for the same reason.

**Within the budget, the next container goes to whichever project is holding fewer.** A
global budget bounds the machine; on its own it does not stop a project with a thousand open
issues from holding both slots for ever while a project with one waits behind it.
`NextInFrontier` orders the queue by each project's held count and breaks the tie by age, so
a project with work waiting is served as soon as any container frees — and a project on its
own still gets the whole budget, which is what "bounded" has to mean rather than "rationed".
`ContainerBudgetTests` asserts both halves, because taking "one project per container" too
literally would starve a project that is the only one with work.

**A round waiting out its retry backoff still holds its slot**, which is the generalisation
of #7's rule rather than a detail of it. The attempt ceiling is three *attempts at one
round* and the budget is two *rounds at once*; conflating them is how three waiting retries
would come to occupy a budget of two. A round waiting forty seconds is still a round that
exists, so it still holds one of the two, and a third work item waits in Frontier for a
container that has not been given away. What it must not do is hold a slot for ever, and it
cannot: the attempt ceiling ends the round and the round timeout ends the attempt.

**One gate, on the loop's own step.** With a heartbeat there are two things that can ask the
machine for a step at once — a reviewer's click and a tick — and without a gate the budget
itself would be a race: two callers could both read one round in flight and both start a
second, and the count that is supposed to bound concurrency would bound nothing. So
`StepAsync` takes a `SemaphoreSlim` and `StepOnceAsync` is the body. This is a gate on
another caller and not a wait for anything: no timeout, held only for the length of a step,
and the only one in the process, which `PolicyTests` asserts so that it cannot quietly become
a queue with a deadline. It is held across the merge an approve asks for, which is a merge
that hangs rather than fails — bounded by the seam's own client (#10), not by anything here.

### The heartbeat

`Driving/FactoryDriver.cs` is the driver every prior ticket recorded as missing: a real
agent, a real container, a real deriver and a correct retry policy all exist, and until now
nothing stepped the machine on a schedule, so in production a work item in Backlog only
moved when a reviewer's click asked for a step. **It is a tick, not a wait.** Each tick asks
intake for one project's turn and the loop to apply whatever it can until it has nothing
left, and returns. Nothing is held up: the loop applies one transition per step, so a tick's
work is bounded by what the machine has to apply rather than by a clock, and
`FactoryConstants.HeartbeatInterval` (five seconds) is the only thing in the process that
has to find out for itself that time passed.

**It respects the budget by not being the thing that enforces it.** The driver has no
opinion about how many containers are running — it does not know, cannot know, and is not
asked. It asks the loop for a step, and the loop is the only component that counts rounds, so
a tick with a full budget applies a landed round, a decision or a merge if there is one and
otherwise does nothing at all. That is why the budget and the driver land together: a timer
with nothing behind it is a factory spending a container on every issue of every project at
once, which is the failure #8 refused.

**The `Task.Delay` in `ExecuteAsync` is the one legitimate exception to "nothing waits", and
`PolicyTests` was changed deliberately and visibly to say so.** It used to read "there is no
`Task.Delay` anywhere in the assembly", which was right until the heartbeat landed and wrong
the moment it did. Rather than weaken it to "`Task.Delay` is allowed now", it was *tightened*
to name the single permitted call site: one `Task.Delay`, in `FactoryDriver.ExecuteAsync`,
counted as well as named, and nothing else anywhere. A second one — in the loop, in the
deriver, in the poller, or a second in the driver itself — fails the suite, exactly as a
second one did under the old blanket ban. `Thread.Sleep`, `Timer` and `CancelAfter` remain
forbidden everywhere, and the deriver's own "nothing waits here" check was re-pointed at
*the deriver* rather than at the assembly so that it cannot become the thing that quietly
forbids the heartbeat again.

**The poller stays serial, deliberately.** #4 left this as a note: intake is a read against
one seam, a pass is already bounded per project, and parallelising it would buy a little
latency in the one part of the factory that was never the bottleneck while costing the
rotation order — which is what makes "one noisy repository cannot starve the rest" provable
at all. The driver *steps* the poller, so intake now runs on a schedule; the schedule is
intake's own (`PollInterval`, compared against `IClock`), and a tick before one is due is a
no-op rather than a read.

**The driver is not started in the test host.** `FactoryApp.Create` takes
`drivingTheMachine` and `FactoryHost` passes false, because a live five-second tick running
alongside a test that asserts on the state of the machine would make every test in the suite
a race against a timer. The driver is registered either way, so a test resolves it and calls
`TickAsync()` — a method separate from `ExecuteAsync` precisely so a test can move the
machine without a clock of its own and without a sleep.

### Testing concurrency without sleeping

`Concurrency/ContainerBudgetTests.cs` and `Concurrency/ProjectBoardTests.cs` settle every
concurrency question by **holding a round open and stepping the machine**, never by timing
one. A round that has been asked for and not yet returned is a round the factory is inside,
so `FakeNOpenCode` counts how many were inside at once and the tests assert on that **peak**.

That distinction is the whole difference the ticket draws, and it is worth stating plainly: a
test that waits for every work item to reach Review passes just as well against a factory
that ran the rounds one after another as against one that ran three at once. The peak does
not. `The_budget_bounds_how_many_rounds_run_at_once`,
`The_budget_is_never_exceeded_no_matter_how_many_work_items_are_waiting`,
`A_long_build_in_one_project_does_not_delay_another_projects_issue_from_starting` and
`A_noisy_repository_cannot_take_the_whole_budget_from_a_project_with_one_issue` all assert
on the count rather than on the end state, and all of them are hermetic — no Docker, no
model, no sleep, no port. `A_reviewers_approval_merges_with_the_budget_full` makes the
budget full with rounds that are stuck and never released, so the merge below is made with
the budget full and there is no timing in it at all.

**One test that does not prove what it might seem to, stated rather than hidden.**
"A merge is not blocked by a full budget" is three tests rather than one, because the
48-hour threshold cannot be made to come due while two rounds are genuinely in flight: the
rounds must have started *after* the work item entered Review, and the threshold is 48 hours,
so by the time it fires both rounds are long past the 90-minute round timeout. It is
therefore asserted through the two paths that *can* contend with a full budget — a
reviewer's approval, and a parked merge's ten-second retry backoff — plus the structural
check that `MergeWhatNobodyReviewed` reads no in-flight state. That is weaker than a
behavioural test against the timeout path, and the reason is a property of the two constants
rather than of the tests.

### The board, by project

The board groups work items by project inside every lane and can be narrowed to one, and
grouping is the visible half of the budget: a work item in Frontier is queued rather than
refused, and a reviewer who cannot tell whose work item that is cannot tell whether the
factory is working on their project or somebody else's.

- **Grouping is inside the lane**, because the lanes are the board's spine and a lane is a
  state every project shares. Each group carries its own count, which is the question a
  reviewer is asking when they group: how much of this lane is mine.
- **Filtering is a GET with `?project=`**, a read and not a write path. It changes what is
  rendered and nothing else: no work item moves, no decision is recorded, and the three
  decisions are offered and refused on exactly the same terms whatever the filter is
  (ADR-0005, ADR-0008). A filter that narrowed the decision set would be policy on the page.
- **A filter renders the diff too.** A narrowing that dropped the change would narrow what a
  reviewer can judge, and would be the first thing on the board to differ between the filtered
  and unfiltered views of the same work item. Asserted in
  `A_filtered_board_still_renders_the_diff_and_still_refuses_a_fourth_decision`, which also
  presses a fourth decision on the filtered board and gets the same refusal.
- **A filter naming a project the factory does not serve shows nothing and says so.** An
  empty board is two different facts a reviewer cannot tell apart — this project has nothing
  waiting, or the factory has never heard of it — and the second would look like the first
  working perfectly. So the board states which filter is in force and whether the factory
  knows that project. Every project is always on offer, including the one in force, so a
  reviewer can get back to the whole board without a browser's back button, and a decision's
  redirect keeps them on the project they were looking at.
- **The budget is rendered** — "2 of 2 worker containers in use" — because a bounded machine
  that says nothing about its bound is indistinguishable from a wedged one. A work item in
  Frontier behind a full budget is queued; one behind an empty one would be a fault worth
  seeing.

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

Still not here, and deliberately: the merger (`#10`) and the diff view (`#11`). The agent and
the result deriver are here — see **The worker container**. The container budget and the
driver are here too — see **The loop**. Retry classification and backoff are here too: see
**Failure paths and retry** below. Nothing
sleeps, defers or times out in any of this: the ceiling is a count and the threshold is a
comparison against `IClock`.

## Failure paths and retry

Two classes, and the whole ticket is the difference between them. `Failures/FailureClass.cs`
has `Transient` and `Permanent` — DESIGN.md's own words — and **classification is a type,
never a message.** `TransientFailure`, `PermanentFailure` and `WorkerContainerException`
each *are* their class, so a component that observed a failure decides at the point it
observed it, by throwing the kind of failure it means. There is nowhere in the factory
where a failure becomes transient by having a word in it, and `Failures.Classify` reads
anything that has not declared itself as `Permanent` — the safe default, because an
unclassified unattended retry is what parking a failed merge exists to prevent.

**Where each classification is decided, and why there:**

- `ContainerRuntime` — a `docker create`/`start`/`logs` that failed. It is the only
  component that knows the verb, and every one of those is a call to a daemon that was
  either answering or not. Transient. `docker rm` failing is not a round failure at all,
  so it is logged and nothing else.
- `DockerCli` — no CLI on the PATH. Permanent: the binary is either there or it is not, and
  a second attempt in ten seconds begins a second identical failure.
- `WorkerRoundRunner` — no project file being served, permanent (configuration does not hot
  reload); a broken container, the runtime's class carried up; a round that *ran and wrote
  no result*, permanent. Note the last one. A missing result file and a `docker cp` that
  failed are **the same signal** at that boundary — both are `cp` exiting non-zero — so
  telling them apart would mean reading the reason out of the message, which is the
  guessing the policy refuses. It is therefore *not* classified, and the runner calls a
  round that ran and produced nothing permanent. Do not "fix" this by parsing the output.
- The loop — the round timeout, which the spec's own state machine gives a row of its own:
  `In Progress | round timed out | Escalated`. Not retried, because a round that hung for
  ninety minutes is the most expensive thing the factory has and would very likely hang
  again. It is still ended and its token still cancelled.
- `IGitHub` — #10's to declare. Until it does, every merge refusal is permanent, which is
  the honest reading of "we do not know why this failed".

**A build that fails its tests is structurally unretriable, not merely un-retried.** A
round that ran and whose change failed comes back `Produced` with the failing exit code in
the payload; a `Produced` round carries no `Failure` at all, and `RoundResult.IsRetryable`
is `Failed && Transient`. There is nothing for a retry policy to act on.
`PolicyTests` asserts by IL scan that `IsRetryable` is read in exactly one method, so a
second opinion cannot grow next to it.

**A transient round failure retries; the round does not end.** Three attempts total, then
Escalated. The round is asked for again once the backoff has passed, as the *same* round —
a container that would not start is not an attempt at building anything, so it is not
charged to the round ceiling, and the board's "round 1 of 3" stays honest. `RoundCount` and
the attempt count are different things and both are recorded (`RoundResultRecord.Attempts`).
While a round waits out its backoff the work item stays in In Progress and keeps its slot,
because a round that has not ended has not released one.

**A merge retry is the deliberate widening of Escalated, and it is not the 48-hour loop.**
`WorkItem` carries `MergeAttempts` and `MergeRetryAfterUtc`; `RecordMergeFailure` moves both
in one write. A transient merge failure gets up to three attempts with the same backoff; a
permanent one gets exactly one, ever. Four things make it not the loop #6 refused: it is
**bounded at three**; it is **classified**, so an unclassified refusal is not retried; it
is **paced by a backoff that grows**, not by a 48-hour threshold; and it is **counted on the
work item**, so a restart cannot hand a merge the repository has already refused a fresh
budget. Above all it is **reachable only by a work item that has a merge failure of its
own** — a work item parked by a failed build, by spent rounds or by a decline has no such
record, so nothing here can merge it however long it sits. A reviewer's new decision clears
the count, because approving again is a second complete decision, not a continuation.

**A parked work item is never re-opened. Not by the loop, not by a decision.** This was
#6's to leave and it is ruled here: a parked work item is finished by a human, merged or
declined, and those are the only two ways out of Escalated. Re-opening the build would mint
a fresh retry budget for the same failure *and* spend rounds that are a reviewer's to spend.
So a work item parked by a failed round can end up with two of its three rounds unspent,
and they stay unspent — the honest cost, and said rather than left to be discovered. The
board's decision set has not grown by one, which is the check `PolicyTests` makes.

**Intake's backoff composes with the poll interval rather than competing.** A backoff can
only lengthen the wait, so the 60s pass is a floor and the backoff is a ceiling on how
often the factory asks. The first few waits (10s, 20s, 40s, 80s) are *shorter* than the
interval, so the interval paces a project for its first few failures and the backoff takes
over only once it has grown past it. A permanent poll failure is given no wait of its own —
the pass cadence is already the slowest this factory asks anything. Nothing is escalated,
because escalation is a work item's state and a repository that cannot be read has produced
no work item to park. `PollBackoffCeiling` (16 minutes) is where the growth stops: a project
down for a day is read a handful of times rather than 1440, and one that recovers is picked
up within sixteen minutes.

Nothing here sleeps, defers or times out. A backoff is a `TimeSpan` compared against
`IClock`, and a step asked before the wait has passed does nothing at all. `PolicyTests`
scans the application IL for `Task.Delay`, `Thread.Sleep`, `Timer` and
`CancellationTokenSource.CancelAfter` and requires none of them **in the policy** — the one
exception is the heartbeat's own tick, named and counted, under **The heartbeat** above —
which is also why a bounded GitHub read is left to the seam's own client (#10) rather than
given a timer here, the same answer the merge call already has.

**A retry now waits for a heartbeat rather than for a reviewer.** That is the whole of what
the driver changed for this policy, and it is worth saying precisely because a retry is
exactly the thing that must *not* fire on a schedule of its own. The backoff is still a
comparison against `IClock`; what changed is that something now asks the loop to look, so
the wait is ended by the driver's tick rather than by a human happening to click. A
transient failure is not retried because five seconds passed — it is retried because a step
was taken and the step found the wait had passed. `The_heartbeat_steps_intake_as_well_as_the_loop`
asserts that a tick before the poll interval is a no-op rather than a read, which is the same
property on the intake side.

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

- **A permanent merge failure is never retried by anything, on any schedule, ever.** Telling
  a transient failure from a permanent one, how many attempts, and how soon, is the retry
  ticket's — now landed, and described under **Failure paths and retry** below. So the
  decision is recorded *applied*, not pending: a pending decision is retried on every step
  and across every restart. Because Escalated is decidable, the board still offers the
  reviewer the decision form, and approving again is a second, complete, human-attributable
  decision — with its own attempts — rather than a silent re-run. `ApproveTests` asserts
  the applied-not-pending rule, including across a restart.
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
it. The project filter is on the reading handler and is not a third of either: a query
string that changes what is rendered and nothing else, which
`A_filtered_board_still_offers_the_three_decisions_and_still_refuses_the_fourth` asserts by
pressing a decision on a filtered board and by posting a fourth by hand to it.

The board does not move work items, and it does not merge anything. It records what the
reviewer decided, in their own words, and asks the loop for one step; which swimlane a
decision means is the loop's policy (ADR-0005), so a page that decided lanes itself would
be a second state machine that could disagree with the loop about the same work item. That
one step is also a *second* driver rather than the only one, now that the heartbeat steps
the loop between decisions, and it is deliberately kept: a reviewer's click has to do
something now rather than at the next tick, so the decision is carried out while the reviewer
is holding the response. The two are not in conflict because the loop takes one step at a
time (`Orchestrator.StepAsync`'s gate), so a tick and a click cannot interleave inside one
step — they queue, and the board's own step is the one the reviewer is waiting on.

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
