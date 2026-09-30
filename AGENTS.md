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
`IGitHub` is implemented too: `GitHub/GitHubClient.cs` is the real GitHub API, and it is
the process's **first and only `HttpClient`**. That is deliberate and it is checked —
`The_process_holds_exactly_one_http_client_and_the_merger_is_it` names it, so a second
component reaching for the network is a failing test rather than something a reader has to
notice. The registration is `TryAdd` as the others are, so the test host's fake still
wins. The `GitHubNotBuiltYet` refusal that stood in its place is gone rather than left
behind, and `A_seam_with_nothing_behind_it_refuses_rather_than_doing_nothing` went with it
— replaced, visibly, by `The_github_seam_is_a_real_client_and_a_call_it_cannot_make_says_which_one`,
which keeps the guard it was (a seam that quietly did nothing must fail) aimed at the
failure it can still have (a call that cannot be made must say which).

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
-m "$AGENT_FACTORY_MODEL" --file "$out/brief.md" -- "$AGENT_FACTORY_AGENT_PROMPT"`, and both
the brief and the prompt are the factory's own, with the reviewer's words quoted in and never
expanded. Four details there are deliberate:

- **`--auto` is required, not a convenience.** Without it the CLI stops and waits for a
  human to approve each edit, and there is no human inside a worker container. What it
  widens is the agent's freedom *inside* a container that holds no write credential, has no
  host path and publishes no port (ADR-0006, ADR-0010, ADR-0012), so there is nothing
  outside it for a confused or injected agent to reach.
- **`--standalone`** gives the round a private server that dies with the process, rather
  than the CLI's background service. The worker README names that service as a loopback
  listener; this way there is not one.
- **The model travels as `-m "$AGENT_FACTORY_MODEL"`, and project config is switched off.**
The project states the full `provider/model` reference (#38) and the factory passes it through
verbatim; `OPENCODE_DISABLE_PROJECT_CONFIG=1` is set because a round tree's own `opencode.json`
can set the provider endpoint, and a tree-chosen endpoint is the round's credential leaving for
wherever the tree pointed it. The credential itself is re-emitted under the canonical name the
provider's tooling reads (`ANTHROPIC_API_KEY`), derived from the model prefix — the project's
own name for the key stops at `ICredentialReader`.
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

### Credentials: one goes into a container, one does not

`Credentials/ICredentialReader` is the **only** thing in the process that can turn a
credential *name* into a credential *value*, and it now has **two** callers.
`PolicyTests` asserts both by IL scan, and **this claim changed deliberately and visibly
for the merger** — it used to be "one caller", and it stopped being true the moment a
merger existed. It was not weakened to accommodate the client. The shape of the design is
what changed, and in ADR-0006's own words: the host "retrieves the commit, pushes the
branch, and opens the pull request under the factory's own token", so a factory that ships
changes and never resolves its own GitHub token would be a factory that cannot ship
anything. Two readers is what that decision asks for. What did **not** change is the
direction, and that is what the two halves of the test assert:

- **The container boundary is one method.** A worker's environment is built in exactly one
  place — `WorkerRoundRunner.RunRoundAsync` — and the only credential read on that path is
  `EnvironmentFor`'s, which is the LLM key. So a credential value can only reach a worker
  by going through the component whose one read is the LLM key's.
- **The LLM key goes in.** The agent cannot reach a provider without it. It is scoped to
  one project, lives for one container, and is never logged. When the environment does not
  have it the round **still runs** and a warning says so — refusing there would be the
  factory deciding for itself that the agent needs one, and a round handed nothing that
  says nothing reads on a board as a round that needed nothing.
- **The GitHub write token never does.** The round runner never passes that name to the
  reader, so the value is never in the runner's hands and cannot be handed over even by
  accident. The merger resolves it on the **host**, after the container is gone, where
  there is no container to hand it to.
- **The merger uses it in two places and logs it in neither**: an `Authorization` header on
  each request (never a query parameter, never a URL, because a token in a URL is a token in
  a proxy's access log) and the environment of the one `git push` child. The *name* is
  logged, because a name is not a secret and it is what an operator has to go and fix.
  `The_credential_is_asked_for_by_name_and_never_appears_in_anything_the_factory_says`
  checks the value against the formatted log lines, the exception a reviewer reads on the
  board, and every request's path and body.

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

### When a round's own command failing is a result, and when it is a failure

**This is the whole of #22, and it is the one place the design's own sentence needed
correcting rather than extending.** That sentence — *a round whose own commands failed is
still `Produced`* — is right for a failing test and wrong for an agent that was refused
before it began, and for a long time the two were the same signal. The second real run hit a
provider rate limit three times: `opencode run` exited 1 in about two and a half seconds,
wrote nothing, and the card said `data-outcome="Produced" data-diff-state="empty"` over the
sentence *"Empty. Nothing on disk differs from the commit the round started at."* The
reviewer's first two decisions were made against that.

**There are two exit codes in a result file and they are not the same thing.**

| | Where it is | What it says |
| --- | --- | --- |
| The round's **own** exit code | `roundExitCode`, in the result file's header | whether the round ran to completion |
| A **command's** exit code | `exitCode`, on each `CommandOutcome` | what the work did |

`RoundResultDeriver` reads the first into `RoundEnvironment.RoundExitCode`, which for the
whole first life of this factory was written into every result file and read by **nothing** —
a grep across the application returned no matches at all. That is fixed, and
`PolicyTests.A_rounds_own_exit_code_is_read_in_exactly_one_place_and_no_command_exit_code_is`
now holds the shape: the field is read by the deriver's header reader, put in front of a
reviewer by the payload, and **decided on in exactly one place**, `WorkerRoundRunner`.

**The boundary is the round's own exit code, and it works because of the round script.**
`WorkerRoundRunner.RoundScript` is fixed, and its last statement is the `opencode run`
invocation with nothing after it. A shell's exit status is its last command's, so
`roundExitCode` **is** the agent's exit code — a structural fact about the script the factory
wrote, not an inference. Adding a line after the agent would silently move the boundary, and
`WorkerRoundRunnerTests` says so at the constant.

- **`roundExitCode == 0`** — the round finished. Whatever the commands inside it returned is
  **data**: a build whose tests failed is `Produced`, in Review, with the failing exit codes
  in the payload for a reviewer to read. This is ADR-0001 and it has not moved.
- **`roundExitCode != 0`** — the round did not run to completion. A rate limit, a refused
  provider, a missing model, a brief the agent would not accept. Nothing on disk is a finished
  result, so the round is `Failed`, **transient**, and worth another attempt.
- **`roundExitCode` absent or `null`** — the round has not said how it ended, and nothing is
  claimed. Reading a missing field as a failure would be the guessing the whole classification
  policy refuses, in the one place where guessing parks a work item that was about to work.

**The two cases never meet, and that is the point.** They are told apart by *which number* was
non-zero, not by a flag both could set, and a boundary written as "did anything come back
non-zero" would make every failing test retryable. The `PolicyTests` pin above is written as
the negative form — *no reader of a command's exit code may live in the loop or the round
runner* — because a list of reader names would have to name the compiler's own artefact for a
lambda, and because a list is a statement about today while the negative form is a statement
about the rule.

**Nothing in `worker/` was edited to achieve any of this**, and nothing needed to be: the
image already wrote the number, and the image's own README says from the other side that
`docker run`'s exit code "is about the container, not the round" and that "a round whose last
command failed still exits 0 with `roundExitCode` inside the file". Two other causes named in
#22 are deliberately *not* fixed here, and the reason is the same: they are consequences of
that one.

- **`docker logs -f` exits 0 whatever the container did.** So the runtime's transient
  container-failure classification never fired for a dead agent. It is left alone because it
  is not the channel that carries the answer: the result file is, and the runtime's own
  failure paths — a `create`, a `start` or a `logs` that the daemon refuses — still classify
  and still retry correctly.
- **`worker-round` exits 0 whenever it wrote a result file.** Also a consequence, and also
  already documented in the image.

**A failed round carries its payload.** `RoundResult.Failed` grew an optional `payload`
argument for this, and the round runner passes one. A rate-limited round's payload is the
only account of what stopped it — the exit code and the provider's own words — and dropping it
would replace a false claim with an empty card. It does not change `IsRetryable`, which is
still `Failed && Transient` and is still read in exactly one method.

The deriver has **no field saying which commands were tests** and none may be added: the
repository's own scripts decide what tested means, and a factory that guessed would be
imposing a test convention (ADR-0011).

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
beside the store. It is **kept** after the round: the host has to be able to reach the round's
commit once the container that made it is gone, because the host pushes and not the container
(ADR-0006). `GitHubClient` now turns that tree into a pull request, and **it is still not
cleaned up** — see **The merger's files** below for why, and for the read-only pack files that
a cleanup would have to clear first.

It is kept for a second reason now, which is the review surface — see **The diff in
Review** below. The board's diff is `git diff` against that directory, which is why the
directory is not cleaned up after a round.

### One directory per round, and per attempt, and never per work item

**This is the other half of #22, and it is a filesystem fact rather than a modelling
choice.** `docker cp` does not replace a destination directory: it copies the source *into*
it, under the source's own name. Verified against the real daemon, and reproduced in
`FakeDockerCli` and asserted by
`ContainerRuntimeTests.Lifting_the_same_path_twice_nests_it_rather_than_replacing_it`:

```
cp into a destination that does not exist:  tree/top.txt, tree/sub/file.txt
cp into a destination that already exists:  tree/top.txt, tree/sub/file.txt,
                                           tree/work/top.txt, tree/work/sub/file.txt
```

The landing directory used to be built from the work item alone, so round 2's tree landed at
`tree/work/` inside round 1's. The host diff is `git diff` in `tree/`, so **the board's diff
for round 2 was round 1's diff**, and the merger read the same path — an approve on round 2
would have pushed round 1's commit while the reviewer believed they were judging round 2. On
the run that found it the two trees happened to be identical, so nothing incorrect shipped;
that was luck, not a property. The second-order effect was worse: a nested copy leaves `git
status` reporting `?? work/`, which is exactly the merger's *"left changes uncommitted"*
refusal, so **every round after the first was permanently unshippable for a reason unrelated
to its work.**

So the layout is now:

```
rounds/<work item id>/round-<n>/attempt-<k>/
                                         result.json
                                         tree/          <- ContainerRuntime.RoundTreeFolder
```

`Round` carries `RoundNumber` and `Attempt` for this, and the loop is the only thing that
knows either — `Orchestrator.RoundFor` fills them in, and
`WorkerRoundRunner.LandingFor(options, round)` is the one place the path is composed. **The
*attempt* is in the path as well as the round, and a per-round path alone would still have
been wrong**: a round that failed transiently is asked for again as the *same* round, so a
retry's tree would have landed inside the attempt it is retrying.

**The merger reads the path off the round's record rather than recomputing it.**
`GitHubClient.TreeFor` walks the work item's rounds newest-first and takes the first tree that
is on this host, which is what makes *"the merger and the board read the same one"* a
structural property rather than a convention two components have to keep agreeing on. It is
also what `Merging.cs` now sets up: the fixture records a round rather than arranging for the
client to look somewhere convenient, so a test cannot pass by pointing the merger at a tree
it put elsewhere. A round whose tree never came out is **not** a reason to ship an earlier
round's, so the walk stops at the first real tree and refuses if there is none.

## The merger

`GitHub/GitHubClient.cs` is the real GitHub API and the whole of the factory's knowledge of
it. One `MergeAsync(repoUrl, issueNumber, ct)` is the only way a change ships — push, open,
merge — and the order it does them in is the whole of this section's argument:

**Every request carries a `User-Agent`, and that is a requirement of the service rather than
politeness.** GitHub *refuses* a request without one — a 403 whose body says so — so a
client that omits it cannot read a repository, cannot open a pull request and cannot merge
anything. It is set on each request in `AskAsync`, the one place a request is built, rather
than on a configured client's default headers, so "every request carries one" is a property
of the code path rather than of a setting somebody can drop. #16 is what it cost to find
out: the client had no `User-Agent` for its whole first life, every call it made was
refused, and **the entire external surface of this factory had never worked** — no intake,
no merge, ever.

1. Resolve everything that can be refused without leaving the building — the project, its
   credential, the work item behind the issue number, the round's tree on this host, and
   the commit that tree is on.
2. **Look for a pull request on this branch. Before anything is created and before the
   branch is pushed.**
3. Push the commit, unless the branch is already on the remote at exactly that commit.
4. Open the pull request, or use the one that is already there.
5. Merge it, and only if the branch still carries the commit the reviewer judged.

**The read at step 2 is the idempotency, and nothing else in the method is.** Every attempt
for one work item derives the *same* branch, because the branch is named deterministically
from the issue number and the work item's title
(`agent-factory/<issue-number>-<slug>`). So "the pull request for this branch" is a question
with one answer, and a retry after a push that landed and a merge that did not finds the
pull request the first attempt opened and goes straight to merging it. A branch named after
a commit or a timestamp would make every attempt a new branch and every one a new pull
request. `A_push_that_lands_and_a_merge_that_then_fails_does_not_leave_a_second_pull_request`
is the test, and it asserts against a real bare repository on the machine that there is one
branch and one pull request after two `MergeAsync` calls.

Four other things make the same call safe to make twice, and each is a refusal rather than
a convenience:

- **An already-merged pull request is a success, not a second merge.** `Done` means merged,
  the change is merged, and there is nothing left to do. A second approve or a retried
  parked merge says so and creates nothing.
- **A closed, unmerged pull request is refused permanently.** A human closing it is a
  decline, and the asymmetry the whole loop is built on applies: a decline is conclusive
  and the factory does not go around it. Re-opening and merging would be shipping a change
  somebody closed, unattended.
- **A branch that moved on is refused permanently** — at the push (there is no `--force`
  anywhere in this factory, so a branch somebody else owns is not overwritten) and at the
  merge (if the pull request's head is not the round's commit, the commits on it are not
  the ones the board showed anybody).
- **GitHub's own 422 closes the read-before-write race.** Two things asking at once both
  find nothing; the second create is refused, and the client closes that window by asking
  again rather than by reading the 422's message.

**The pull request carries the work item's identity both ways.** The title is the issue's
own title, unchanged, so the two things a reader compares are the same string. The body's
first line is `Refs #42`, and **the issue is linked rather than closed** — that is a
decision, and `A_pull_request_body_can_never_close_the_issue_it_answers` holds the rendered
body against GitHub's own closing-keyword list. Merging a pull request whose body says
`Closes #42` closes the issue, and this factory can merge a change *nobody reviewed* (at
the 48-hour threshold, or on a retry of a merge that failed), so closing somebody's issue on
the strength of that is a second claim the merge has not earned. Nothing is lost by leaving
it open: intake is idempotent on `(repo_url, issue_number)`, so a still-open issue is found
again and changes nothing about a work item that is already Done.

**The branch is a git refspec, an HTTP path segment and a pull request body**, and an
issue's title is a stranger's words, so the slug is reduced to `[a-z0-9-]` and nothing else
can survive into it. A title with nothing sluggable in it gets a fixed word rather than an
empty ref component.

**The work item is the source of the title, base branch and tree path, not GitHub.**
Reading the issue back from the API would be a second source of truth about the same thing,
free to disagree with the board the reviewer read — and since the branch name is derived
from the title, a title edited on GitHub between the round and the merge would move the
branch out from under a push that had already happened. So `GitHubClient` reads
`IWorkItemStore`, which is the one piece of factory state the adapter holds and is stated
here because it is a real coupling.

**A round that left changes uncommitted is not shipped at all.** The board's diff is `git
diff` against the base of the *working tree*, so it shows a round's uncommitted files along
with its commits; a push sends only what was committed. Shipping the committed part and
calling it the reviewed change would make `Done` mean something the reviewer never saw, and
committing the difference on the host would make the host the author of work nobody
reviewed (ADR-0006). So the two are refused against each other, permanently, and a human
finishes it from the board.

**The merge is a merge commit** (`merge_method: "merge"`), not a squash. A squash would
rewrite the round's commit, so the change on the base branch would not be the commit the
reviewer judged, and reverting would not be one action against one commit. The spec asks
for every merge to be a real pull request that can be reverted, and this is the shape where
that is literally true.

### The merger's files

**Lifted trees are not cleaned up, and the decision is #11's to revisit rather than this
ticket's.** Three reasons, and the first is the one that binds: the review surface reads
`diff_tree` off the round record and the card prints that path, so a tree is still evidence
a reviewer is being pointed at days later — including for a work item that was **rejected**,
which never reaches a pull request and so has no other copy of its change anywhere. The
board stores the diff rather than re-running git, so nothing regenerates it. And there is
no retention policy in this factory at all; a delete is irreversible and unreviewable, and
one process's disk is not yet a problem worth solving by losing data.

If a tree is ever cleaned up, it has to clear **read-only object files** first: a tree
lifted out of a Linux container arrives on Windows with read-only pack files and a recursive
delete refuses to remove one. `LiftedTree.WithReadOnlyObjects` makes that the normal case in
the tests rather than an edge one, and its `Dispose` and `BareRemote.Dispose` both do the
walk. **The merger itself never writes to the tree at all** — the push is a read of its
objects and a write to the remote, and `GIT_OPTIONAL_LOCKS=0` is set so git's index refresh
cannot be the thing that fails — which is why the merger needs no such handling.

### Testing the merger

Three layers, and the honest split.

`GitHub/MergeTests.cs` is the load-bearing one: the real client, real git, a real tree with
read-only object files, and a real **bare repository on this machine** as the push target.
That last part is what makes the idempotency test worth anything — a push that half-succeeded
is a property of a remote, and asserting it against a stub would be asserting the stub. A
directory git accepts as a remote is a remote; what this cannot exercise is HTTPS
authentication, which is why the credential is covered where it is actually decided (the
header and the child's environment) rather than there.

`GitHub/GitHubApi.cs` is the faked transport. The seam is `HttpMessageHandler`, so the
client, its classification, its git invocations and its own ordering all run for real and
only the network is gone. It records every request's method, path, query, body and headers,
so the assertions are about requests and about repository state rather than about which
class called which. **It filters `state` on the pull request list the way GitHub does**,
because a fake that answered every state the same way would let a client that asked for
`state=open` pass — and a client that cannot see a merged pull request ships a change twice.

**One thing about GitHub is now reproduced rather than recorded, and it is the
`User-Agent`. #16 is why.** Everything else in that fake is a fake that agrees with the
client by construction, which is exactly right for shape and ordering and exactly wrong for a
requirement of the service: a fake will cheerfully answer a request github.com refuses
outright. So 317 tests passed against a client whose every request carried no `User-Agent`
and would have been refused by all of them — the seam was tested against something that
agreed with it because it was written to. A fake cannot catch what the real service
requires, and the next adapter in this process should take that as the reason to read the
real API's own list rather than to write another transport that only records. Two things
hold the header, and both are worth knowing about:

- **`GitHubApi` refuses a request with no `User-Agent`**, with GitHub's own 403 and its own
  message, before any scripted route is consulted. That is what turned 23 tests red when the
  header was removed rather than the two that name it, and it makes every test in this
  layer one that could have caught the defect.
- **`UserAgentTests.Every_request_the_client_makes_carries_a_user_agent`** asserts the claim
  at the transport boundary, over every shape of request the client can send — intake's two
  reads and the merger's four calls — so it is about what goes on the wire rather than about
  what the client was configured with.
  `A_request_with_no_user_agent_is_refused_exactly_as_github_refuses_it` is the guard's own
  test: a check that never fires looks exactly like a check that is not there, so a bare
  request has to be turned away and the same request with the header answered.

`GitHub/GitHubResponseTests.cs` is the classification table as a table, so every row is
checked as a decision about whether a work item is ever tried again.

**What is not covered, and cannot be without a token.** There is no GitHub credential in
this environment, so nothing here runs against github.com. The real API's shape — the exact
field names, whether a 405 really does arrive for a conflict and for a raced base both, what
GitHub actually sends in a `Link` header — is taken from its documentation and is not
verified here. What *is* verified is everything the factory decides: what it asks for, in
what order, what it does with each answer, and what it does when the same call is made
twice.

**One test does not prove what a reader might assume.** The classifier's `Unclassifiable`
list is asserted to have four entries by name, which is a check that the documentation has
not shrunk — not a proof that those four are the only cases. A fifth thing GitHub does that
no status code decides would arrive as a permanent failure, which is the safe direction and
not the right one.

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

**The process now holds an `HttpClient`, and these assertions are stronger for it.** The
merger is the one component that has one — `The_process_holds_exactly_one_http_client_and_the_merger_is_it`
names it — and a check that "the diff reader takes no `HttpClient`" only means something
while a `HttpClient` is something a component *could* have. The property being defended is
availability: a reviewer can judge a change when GitHub is unreachable, and a diff fetched
from the same service that produced the change would be a second opinion from the thing
under review rather than evidence about it.

Four honesty cases the board distinguishes rather than collapsing, each with a test:

| On the card | Means | Not |
| --- | --- | --- |
| `data-state="shown"` | there is a change to read | — |
| `data-state="empty"` | the round **ran to completion** and changed nothing | a round nobody looked at, or a round that did not run |
| `data-state="unfinished"` | the round's own command did not succeed, so the round stopped | a round that chose to change nothing |
| `data-state="unavailable"` | no diff could be generated, and why | a round that changed nothing |
| no `data-diff` at all | no diff on record — an older row, or a round with no tree | any of the above |

**`unfinished` is #22's fourth state, and it is about the round rather than the disk.** The
first real run found a round whose agent was refused by a provider rate limit reported as
`Produced` with an `empty` diff — and *"Empty. Nothing on disk differs from the commit the
round started at"* is a **true statement about the disk** and a **false one about the round**.
The disk claim was never the problem; pairing it with an outcome saying the round had
produced something was. So the disk is still read exactly as before and the sentence is
branched on the outcome, which is the only place the two are read together:

- `Produced` and no change → the round ran to completion and changed nothing. A real
  outcome, and a reviewer is entitled to be told it.
- anything else and no change → the round did not run to completion. Whatever is on disk is
  where it stopped.

The word is `unfinished` rather than `never-ran` on purpose: a round can run for eighty
minutes, change nothing and then be killed, and "unfinished" is true of that where "never
ran" would be a claim the record cannot support. The sentence says which, and points at the
payload that carries the exit code — see **When a round's own command failing is a result**.

**It sits *after* the "there is a change" check rather than before it**, and that order is
itself a claim about the reviewer's need: a round that got partway and then failed has a real
change on disk, and showing it is more useful than replacing it with a note that the round
did not finish. The card's outcome and failure already say the round did not finish; the diff
says what it left behind. `A_round_that_changed_nothing_but_did_not_finish_says_so_rather_
than_being_empty` puts the two states side by side over two byte-identical empty diffs,
because the only thing telling them apart is the outcome.

A round recorded **before** the diff column existed is not retrofitted with an empty one: the
tree it would have been read out of was not kept per round, and a fabricated empty diff would
claim a round changed nothing when in fact nobody looked.

## What a record carries

A record's identity is ambient rather than passed down: `Observability/WorkItemScope.cs`
puts the work item, its project, its issue and (inside a round) the round number on every
record written within the scope the loop opened, so the result deriver, the container
runtime and the Docker CLI can say which work item they are about while taking nothing but
a logger. `Keys` is the whole vocabulary and `A_record_about_a_work_item_is_written_inside_
a_scope_that_names_it` pins both the four keys and the ten components that open one.

**A project-level record has no work item, so it cannot use that vocabulary, and #21 added
a second one rather than a fifth key in the first.** A project whose intake failed has
produced no work item, so there is no id, no issue number and no round to name; the only
identity such a record has is the project, and putting a project's name on it under
`WorkItemProject` would mean a field called "the work item's project" carrying a project
with no work item, while filling the other three keys would mean inventing a work item id,
an issue number and a round number for something that does not exist. So
`Observability/ProjectScope.cs` is its own class with one key — `FactoryProject`, prefixed
for the reason the work item's are, since a record's own message usually names the project
too and a sink that merged the two would emit the field twice — and
`A_record_about_a_project_is_written_inside_a_scope_that_names_it` pins it at its two call
sites, both in the poller. **No existing pin was weakened**: the work item's four keys and
its ten call sites are asserted exactly as before, and the metric tag vocabulary is still
closed at `project` and `outcome`, which this change did not widen — a project-level fault
is on the board and in the log, not in a counter, because a counter would need a tag value
that is a class of intake rather than of a round.

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
same base, same rounds. One repository erroring is contained to its own turn and the rest
of the pass still runs; whether it is asked again, and when, is its classification's
answer, below.

### What intake says on the board

**A project whose intake failed has no work item, so the board's intake section is the
only place its fault can live — and it is rendered in three states rather than as the
presence or absence of work items.** An empty Backlog lane is three different facts: the
repository was read and had nothing open, the factory has never read it, or the factory
tried and was refused. One rendered absence standing for three of them is what #16 found:
the board said "Serving 2 projects" above an empty Backlog and not one poll had ever
succeeded, because the client was refused every request it made. It is the same conflation
the board refuses for a round's diff, and it is fixed the same way — `Pages/HowToReadIntake.cs`
is the whole of the judgement and the view renders what it says:

| On the row (`data-intake-state`) | Means | Not |
| --- | --- | --- |
| `never-polled` | the rotation has not reached this project yet | a project with nothing open |
| `polled` | the read succeeded — with `data-open-issues` saying what it found | a poll that never happened |
| `failing` | the last read was refused, with `data-classification` and `data-again` | either of the above |

Everything a reviewer needs to act on is a field rather than a sentence: the repository,
the classification, the run of failures, and `data-again` — `never` for a permanent
failure, a moment for a transient one, `next-pass` for a project that is fine. **The
section is rendered whenever the factory serves a project, healthy or not**, and its
summary line says out loud that "an empty Backlog does not mean there is nothing to do"
whenever it is not — because a section that only appears when something is wrong is a
section whose absence carries no information, which is the same argument the container
budget makes by always stating its bound. It is above the lanes rather than inside one,
because a project is not a work item, and it is **not narrowed by the project filter**:
a filter is a way of looking at work items, and one that could hide the reason there is
nothing on the board is a filter that can make a broken factory look like a working one.
`IntakeBoardTests.An_empty_backlog_while_intake_is_broken_cannot_be_read_as_nothing_to_do`
is the test that would have caught #16, and it reads the page rather than the poller.

The board asks the poller for this on every render (`IndexModel.Intake` →
`Poller.Intake` → `IntakeRecord`, in `Polling/`) rather than keeping a copy: intake's
state is intake's own, and a second copy would be a second thing free to disagree with
the poller about whether a project is failing. It is in memory and gone on a restart,
for the same reason the backoff was: a poll is a read, and nothing about it is a fact a
reviewer needs to survive a restart (ADR-0009).

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
hangs rather than fails is bounded by the seam's own client — which now exists, and whose
bound is `HttpClient.Timeout` rather than a timer here.

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
that hangs rather than fails — bounded by the seam's own client, whose bound is
`HttpClient.Timeout`, not by anything here.

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
- **A filter does not narrow intake.** It is a way of looking at work items, so every served
  project's row is on the board whatever is in force — see **What intake says on the board**.
  A filter that hid a failing project could hide the reason there is nothing on the board,
  which is the one thing a filter must not be able to do.
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
and obeys the same rule, so there is no second path to `Done`: an ignored work item that
the client cannot ship does not complete either, and it does not report a change shipped
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

Everything this section described is here: the agent and the result deriver — see **The
worker container**; the merger and the diff in Review — see **The merger** and **The diff
in Review**; the container budget and the driver — see **The loop**; retry classification
and backoff — see **Failure paths and retry** below. What is deliberately *not* here is
anything that widens the loop's policy: still no fourth decision, no board write path beyond
the reviewer's form, and no way to reopen a parked work item. Nothing sleeps, defers or
times out in any of this: the ceiling is a count and the threshold is a comparison against
`IClock`.

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
- `GitHubClient` — the whole table is `GitHub/GitHubResponse.cs`, and it is read off the
  status code and two headers rather than out of GitHub's prose. Permanent: 401, 404, 409,
  422, a 405 on a merge whose `mergeable_state` does not explain it, a 403 with no
  rate-limit header (wrong scope, SSO, branch protection), a body this client cannot read as
  the API's shape. Transient: any 5xx, 429, a 403 carrying `Retry-After` or
  `X-RateLimit-Remaining: 0`, and anything that never got an answer at all — DNS, a refused
  connection, a dropped socket, the client's own timeout.

  Four things reach this client that **no status code decides**, and they are named in
  `GitHubResponse.Unclassifiable` rather than guessed at, because a factory that reads
  answers for substrings is guessing in exactly the way the rest of this codebase refuses
  to: a 405 whose mergeable state does not say why (read back the pull request and use
  `mergeable_state`, which is a field); a push git refused (ask the remote — is the branch
  at this commit, and can the repository be reached at all); a merge that answered 200
  without saying it merged (read the pull request back); and an answer that was not the
  shape the API documents.

  Two of those are worth spelling out because they are the judgements rather than the
  mechanics. **A 405 is two facts in one status code** — the change conflicts with the base
  (permanent; only another round or a human settles that) and the base moved while the merge
  was being made (transient; the next attempt is against the new base) — and the retry
  policy needs them told apart, so `mergeable_state` is read rather than the message. **A
  push git refused is classified by asking the remote two questions**, never by reading
  git's output, because git has no status code and its prose is not a contract: if the
  branch is now at the commit that was pushed the push landed, if it is at another commit
  the branch is not ours, and if it is not there then whether the repository can be reached
  at all decides between a connection that was not there and the remote declining.

**A build that fails its tests is structurally unretriable, not merely un-retried.** A
round that ran to completion and whose change failed comes back `Produced` with the failing
exit code in the payload; a `Produced` round carries no `Failure` at all, and
`RoundResult.IsRetryable` is `Failed && Transient`. There is nothing for a retry policy to
act on. `PolicyTests` asserts by IL scan that `IsRetryable` is read in exactly one method, so
a second opinion cannot grow next to it — and
`A_rounds_own_exit_code_is_read_in_exactly_one_place_and_no_command_exit_code_is` holds the
other half, that no component deciding an outcome may look at a *command's* exit code at all.

**A round that did not run to completion is a different case, and #22 added it.** Before
that ticket the only transient round failure was a container the daemon would not start, so a
rate-limited agent was neither retried nor visible as a failure. It is now classified by the
round's own exit code and is **transient** — see **When a round's own command failing is a
result** for the boundary and the argument. The costs of that answer are named because they
are real: three attempts at *one* round, holding its container slot across the backoff, then
the round ends and the work item parks. A genuinely permanent cause therefore spends two
extra containers rather than a reviewer's attention, while the opposite answer would park a
work item a second attempt would have ridden out. What is **not** spent is a round of the
ceiling: an attempt that produced nothing is not an attempt at building anything.

**The two are kept apart by which number was non-zero, and by nothing else.** A round's own
exit code decides the outcome; a command's exit code is data and is read in exactly two
places, neither of which is a policy component. A single "did anything come back non-zero"
flag would make every failing test a transient failure and retry it for ever, which is the
thing DESIGN.md rules out in as many words.

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
over only once it has grown past it. Nothing is escalated, because escalation is a work
item's state and a repository that cannot be read has produced no work item to park — a
project-level fault is rendered on the board instead, under **What intake says on the
board**. `PollBackoffCeiling` (16 minutes) is where the growth stops: a project down for a
day is read a handful of times rather than 1440, and one that recovers is picked up within
sixteen minutes.

**A permanent intake failure is not asked again at all, and that changed with #16.** It
used to be "given no wait of its own", which meant the pass cadence read it anyway: a full
warning every sixty seconds, for ever, for a repository that is gone or a credential that
was never set — an unbounded log generator pointed at a fault no amount of asking touches.
`Poller.Pace` now records the failure with `AgainAfterUtc` null, and `StepAsync` reads that
null as **never** rather than as **no wait**, so the project is read once, says so once, and
is left alone. The two are the same sentence read two ways, and the difference is a
permanent failure against a permanent misconfiguration; it is spelled out in `StepAsync`
because that is where it is easy to get wrong again.

The cost is stated rather than hidden: a permanent failure is cleared by a **restart**,
because everything it is about — a project file, the set being served, an environment
variable — is read at start and does not hot reload. The board says so on the row
(`data-again="never"` plus the sentence), so the operator is told what to do rather than
left watching a board that has stopped asking.
`IntakeRetryTests.A_permanently_failing_repository_is_read_once_and_never_again` holds the
count, the single warning, the state and the restart, and
`A_permanently_failing_project_is_read_once_and_a_transient_one_is_still_being_read` holds
the two schedules apart over a day of passes.

Nothing here sleeps, defers or times out. A backoff is a `TimeSpan` compared against
`IClock`, and a step asked before the wait has passed does nothing at all. `PolicyTests`
scans the application IL for `Task.Delay`, `Thread.Sleep`, `Timer` and
`CancellationTokenSource.CancelAfter` and requires none of them **in the policy** — the one
exception is the heartbeat's own tick, named and counted, under **The heartbeat** above —
which is also why a bounded GitHub read is left to the seam's own client rather than given
a timer here, the same answer the merge call already has. That client exists now and holds
`HttpClient.Timeout`; `PolicyTests` still finds exactly one `Task.Delay` in the assembly
after it landed, which is the check that says the arrangement cost nothing.

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
branch is pushed are the merger's business behind the seam. The loop has no concept
of a pull request, the way it has no concept of a container.

**A merge can still fail**, because the client is now real and a real repository can say
no: a branch that moved on, a change that conflicts, a credential with the wrong scope, a
refused push. Those are the client's failures and the loop parks them, and the three
properties below apply to every one of them. What changed is only that a *healthy* merge
now lands. **The merger's own failures are the ones the retry policy reads**, and they are
declared in `GitHubResponse` rather than guessed at from a message — see **Failure paths and
retry** below.

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
