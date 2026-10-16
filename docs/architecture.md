# Architecture

How this repository is put together. This replaces the architecture sections of
`DESIGN.md` and the ADRs it referenced, which described a factory that ran rounds
in containers and enforced its boundaries with eighteen reflection tests. The
factory now runs a round as a child process on this machine and enforces those
boundaries with the compiler.

The map that decided all of this is
[#54 — the factory as a Clean Architecture codebase](https://github.com/NaniSoft/agent-factory/issues/54),
and each decision below links the ticket that made it.

## Vocabulary

The ubiquitous language is [`CONTEXT.md`](../CONTEXT.md) and it is binding. A
**Work item** is not a ticket, a **Round** is not an iteration, the **Board** is
not an admin, and a component is not a "worker" — the agent is.

## The dependency rule

**Dependencies point inward. Nothing points outward, and `Domain` points nowhere.**

```
                 AgentFactory.Web          endpoints, composition root, Program.cs
                    │        │
                    ▼        ▼
   AgentFactory.Infrastructure    ← SqliteWorkItemStore, GitHubClient, ProcessCli,
                    │               RoundRunner, HostDiffReader, SystemClock, Temporal
                    ▼
         AgentFactory.Application     ← Orchestrator, Poller, FactoryDriver, the seven ports,
                    │                  the merge policy, the Routes/Api projections
                    ▼
            AgentFactory.Domain        ← WorkItem, Round, Swimlane, Decision, Escalation,
                                           DerivedResult, the project's own vocabulary
```

A project reference is the only way one project can see another, and the graph is
exactly the four edges above. `Domain` has **no project reference at all** — not to
the BCL's abstractions, not to anything — which is the property that makes it
possible to point at a type and know it is the factory's own.

### What is a port and where it is declared

All nine ports are **declared in `Application`** and **implemented in
`Infrastructure`**. That is the whole of Clean Architecture's outward rule: the
inside says what it needs, the outside says how it gets it.

| Port | Declared | Implemented by |
| --- | --- | --- |
| `IClock` | `Application` | `SystemClock` (test: `TestClock`) |
| `ICredentialReader` | `Application` | `CompositeCredentialReader` |
| `IProcessCli` | `Application` | `ProcessCli` (test: `FakeProcessCli`) |
| `INOpenCode` | `Application` | `RoundRunner` (test: `FakeNOpenCode`) |
| `IGitHub` | `Application` | `GitHubClient` (test: `FakeGitHub`) |
| `IMerger` | `Application` | `GitHubClient` |
| `IIssuePublisher` | `Application` | `IssuePublisher` |
| `IWorkItemStore` | `Application` | `SqliteWorkItemStore` |
| `ITicketStarter` | `Application` | `TemporalTickets`, `NullTicketStarter` |

**Two interfaces in `Application` are deliberately not in this table, and both are the
same argument.** `ITicketActivities` and `IMergePolicy` invert nothing: each is declared
in `Application` and implemented there — by `TicketActivities`' caller `TicketWorkflow`
and by `MergePolicy` respectively — so they are an *internal* seam, useful for testing
and not a boundary. The `ITicketActivities` half has its own note below; `IMergePolicy`
is the merge policy's own seam, which is why `FakeGitHub` answers it and `GitHubClient`,
the real `IMerger`, does not.

### `ITicketActivities` is not a port, and `Application` names no workflow engine

This table used to carry an eighth row — `ITicketActivities`, declared in `Application` and
implemented by `TicketActivities` — with a paragraph here calling it an **inbound** port,
and a second paragraph added in #76 accepting that it dragged `Temporalio` into
`Application` as a correction to the rule.

**Both are withdrawn: the type is not a port and never was.** A port exists to *invert* a
dependency — an inner component needs something done and must not name the outer thing that
does it, so the contract is declared where the need is and the implementation is somebody
else's problem. Here there is nothing to invert. The workflow and its activities are the
same layer and the same driver: `TicketWorkflow` calls `ITicketActivities` by name, and
`Application`'s contract for the durable path is already `TicketPolicy`, which both drivers
call. `ITicketActivities` is the driver's **internal** seam — useful for testing, and not a
boundary.

The engine resolves every activity call from an `[Activity]` attribute on the very method
the workflow invokes: `ActivityDefinition.CreateAll` skips a method without one, and
`ActivityDefinition.NameFromMethodForCall` throws. So declaring that interface forces the
engine's own vocabulary onto whatever project declares it, and #76 read that as a cost the
inside had to pay. The truth is that the interface was in the wrong place, and the cost was
avoidable — `Application` paid a reference to a workflow engine for nothing.

So `ITicketActivities`, `RouteLanded`, `BuildLanded`, `MergeLanded` and `TicketDecision`
are in `AgentFactory.Infrastructure.Durability` beside `TicketWorkflow`, and `Application`
has **no `Temporalio` reference at all**. The same is true of the `BackgroundService`
`FactoryDriver`: `Microsoft.Extensions.Hosting.Abstractions` is not a framework the policy
core speaks, it is the contract for a long-running task, and nothing in the policy names it.
When a driver *is* the framework's contract the framework is named — but an interface that
merely sits beside a driver is not that, and the rule below stands unamended.

### One policy core, two drivers

The factory steps the same transitions two ways: the in-process loop locally, the
per-ticket workflow when durable execution is configured. The rules exist once, in
`Tickets/TicketPolicy` (in `Application`), which both drivers call — running a round,
landing a build or a routing round, parking, handing back, reading the conversation a
round travels with, and merging. What each driver keeps is only *how* it is stepped and
*how* it is made durable: the loop its step order, in-flight budget, round timeout and
retry backoff, the workflow its history, durable waits and engine retry policy. Decided
in [#64](https://github.com/NaniSoft/agent-factory/issues/64).

The two drivers still disagree about six things, and each is a policy change rather than
a move and so is left alone: how a routing round spends the reviewer's ceiling, what a
transient merge failure does, whether applying a decision marks it applied, the
acceptance guard, how silence past the feedback threshold is handled, and the wording a
parked routing round carries. Those are reported with the ticket rather than quietly
resolved here.

### Cross-cutting types

- `Failures/` → **`Domain`.** A classification of what happened to a round is the
  factory's own vocabulary, not an implementation detail, and `Orchestrator`
  decides on it.
- `FactoryConstants` → **`Application`.** Every value in it is policy: the round
  ceiling, the feedback threshold, the poll interval, the round concurrency.
- `FactoryOptions` → **`Application`.** Configuration is what the inside is told,
  and `Infrastructure` is allowed to read it precisely because it already points
  inward.
- `FactoryPaths` → **`Infrastructure`.** It resolves directories on a disk.
- `WorkItems/WorkItemRecord`, `RoundResultRecord`, `DecisionRecord`, `PendingRound` →
  **`Application`**, beside `IWorkItemStore` rather than beside the store that persists
  them. They read as persistence shape and the first guess is `Infrastructure` — which is
  now also where the thing that maps them to columns is, so the guess looks even righter
  than it did, and it is still not available: a port's signatures name these types, and a
  signature cannot name a type that lives beyond it. What they are is the **port's**
  vocabulary — what the inside says a round, a decision and a pending round are — which is
  something a caller needs in order to call the port at all. No field of any of them is a
  column: no attributes, and every type is the BCL's or the factory's own. The two halves
  touch in one place, `SqliteWorkItemStore`, and in one direction only: it reads the
  records the port declared and writes rows of its own.
- `Observability/WorkItemScope`, `Observability/ProjectScope` → **`Application`.**
  The scope vocabulary is the factory's own ("this record is about work item 4,
  project nexus, issue 42, round 2") and a record is written from inside.
- `Observability/FactoryMetrics` → **`Application`.** It is three instruments on the
  platform's own `System.Diagnostics.Metrics` (the design's four counters, with builds
  succeeded and builds failed sharing one instrument and an outcome tag), and `Orchestrator`
  and `Poller` both take
  it as a concrete type — so putting it in `Infrastructure` would make `Application`
  depend on `Infrastructure`, which is the one cardinal violation this rule has. What
  belongs on the outside is the **OpenTelemetry pipeline**, which is what turns
  instruments into signals — and it landed in [#63](#63) in `AgentFactory.Web`'s
  `Composition/ServiceDefaults`, the outermost project, rather than in `Infrastructure`:
  the pipelines instrument ASP.NET Core, which only `Web` has, and `FactoryMetrics` is
  untouched by it (see **Hosting**, and the note there about `AddMeter`). **This
  corrects the placement this document first gave it**, and the correction is the
  layering rather than a preference: the instruments describe outcomes, which are
  `Domain`'s, and `RoundOutcome` moved to `Domain` with them so the reference is legal
  rather than absent.

### The rules a project reference cannot see

Four references exist today that the compiler would happily allow and that no
slice is allowed to keep:

1. **`Orchestrator` depends on the concrete `Routing.IssuePublisher`** — a field, a
   constructor parameter, an assignment, and one call. Application reaching a
   concrete Infrastructure type is the rule's one real violation. It becomes a port
   on `Application`; the slice that moves `Routing` decides what it is called.
2. **Fully-qualified cross-slice names hide the dependency from a `using`-based
   grep.** `Poller` reaches `Durability.ITicketStarter` fully qualified, and
   `Round.cs` declares a `Routing.RoutingOutcome?` without a `using`. A rule
   checked by reading `using` directives is a rule that can be defeated by not
   writing one. **No cross-slice type may be named fully qualified.**
3. **`GitHub/GitPusher` reuses `Results/HostDiffReader.GitEnvironment`** — the
   merger borrowing the review surface's git-safety configuration. Both end up in
   `Infrastructure`, so this becomes legal, and it is recorded rather than left to
   look accidental: the safety pins are shared on purpose.
4. **`Observability/FactoryMetrics` depended on `Rounds`.** Resolved by moving the
   instruments to `Application` and the outcome they count to `Domain` beside it — the
   instruments describe outcomes, which are `Domain`'s, and `RoundOutcome` is now one.
   The OpenTelemetry exporter, which is the part that really does reach outside, stays
   on the far side of the boundary for [#63](#63).

### The merger's store coupling

`GitHubClient` holds `IWorkItemStore` today, to find the round's tree. That is
legal — `Infrastructure` may depend on `Application` — and it is also the deepest
coupling in the codebase, because two components write the store and one of them
is a REST client.

The rule for it: **an adapter never queries the store.** The merge policy resolves
what is about to ship — the repository, the issue, the title, the tree, the base
commit, the round — into a single immutable plan, and hands that plan to the
adapter. The adapter's whole input is the plan.

### The store of record, over EF Core

`SqliteWorkItemStore` is `Infrastructure`'s now, and it is EF Core over SQLite rather than
hand-written ADO.NET (#62, #77). Four things about that are decisions rather than
mechanics, and each is written at the code that makes it:

- **`Application` names no EF type.** `FactoryDbContext`, its four row types and the
  baseline migration are all this side of the boundary, and `IWorkItemStore` is unchanged:
  a port whose implementation is an ORM is still a port. The class keeps the word *SQLite*
  because the word names the decision rather than the library — the record of
  record is one SQLite file, and *EF* would name the part free to change.
- **The row types are not the port's records, and the duplication is the price of that.**
  A table is a schema — mutable, scalar, one column per property — and the records are
  immutable positional records that the port's signatures name. Mapping them onto each
  other is *possible* for three of the four tables and impossible for `round_results`,
  whose diff is deliberately five independently-nullable columns rather than a shape read
  back as a unit; a model with an exception nobody can state in a sentence is worse than a
  second set of types with one mapping file. The architecture document's own claim is what
  settles it: **no field of the port's records is a column**, and it stays true because the
  columns live on rows.
- **Two timestamps carry a `long` tick converter and the rest stay readable text.** SQLite's
  provider throws `NotSupportedException` on `ORDER BY` and on comparison over a
  `DateTimeOffset`, and exactly two of this store's reads do one of those: the Board orders
  by `created_utc` on every poll, and reconciliation orders pending rounds by `asked_utc`.
  `updated_utc`, `started_utc`, `review_started_utc`, `merge_retry_after_utc` and the rest
  are compared in the loop over a record it already holds, never by a query, so they stay
  legible to the `sqlite3` CLI. Narrower than the research's first list of four, and
  re-derived by running the queries the Board and the loop actually issue.
- **Enums stay text and no foreign keys are created.** Text because `Swimlane`, `Decision`
  and the rest are closed vocabularies this codebase treats as policy and a human should be
  able to read a row; and because a `WorkItemKind` this build does not recognise still reads
  as `Unrouted`, which is what the store has always done. No foreign keys because the
  schema had none, nothing in this factory deletes a work item, and a cascade would mean
  that if something ever did, the record of record would be the first thing deleted.

**The schema is a committed baseline migration, and `data/agent-factory.db` is deliberately
not migrated.** The map decided a fresh database rebuilt from GitHub is accepted, so the
baseline is what a new file is built from and what every future migration is a change
against; deleting the old file and restarting is the entire cutover. The store applies the
migration at start (`Database.Migrate()`), and `FactoryDbContextFactory` exists so that
`dotnet ef` builds a *model* rather than booting the application — which is what keeps a
build-time command from creating and writing the real database. EF also turns WAL on, which
the hand-written store never had: a Board read can no longer block behind a loop write.

### What a slice may not do

**A slice moves code. It does not move policy.** Routing rules, the three
decisions, the round ceiling, the feedback threshold, retry classification and the
indiscriminate poller are all unchanged by this restructure. If a move cannot be
made without changing what the factory does, the slice is wrong, not the rule.

## Testing

Three layers, and the line between them is **what a test's truth depends on** rather
than what it looks like. The restructure's test decision was layered — unit +
architecture + keep the harness — and the durable half shipped first; #86 delivered the
fast layer underneath it.

### Unit — decidable without a host

`tests/agent-factory.unit-tests/` is the fast layer, and it references `Domain` and
`Application` and nothing else. A unit test is a rule of `Domain` or `Application` that
can be decided without a host, a clock, a database, a socket or a process:

- `MergePolicy`, constructed directly with a fake `IMerger` and a fake `IWorkItemStore` —
  the refusals are each a decision (an already-merged pull request is a *success* rather
  than a second merge, a closed-unmerged one is a *permanent* decline, a branch that moved
  on is refused because the commits are not the ones the reviewer judged, a round that
  left work uncommitted is not shippable), and so is the *ordering* that puts every local
  refusal before anything is sent.
- `BranchName` and `PullRequestText`, which are pure and both encode rules with real
  consequences: the slug is reduced to `[a-z0-9-]` because the branch is a git ref, an
  HTTP path segment *and* a body; the branch must be deterministic because that
  determinism is the idempotency; and the body carries no closing keyword, because the
  issue is linked and never closed.
- The closed vocabularies and the classification — `Decisions`, `Swimlanes`,
  `WorkItemKinds`, `Failures.Classify`, `RoundResult.IsRetryable` — where the refusals
  matter as much as the members, and an unclassified failure is **permanent**.
- `GitDiff` and `DiffDocument`, the one reader of git's diff format, and
  `DiffWindow.Showing`'s bound on **whole files** and never mid-hunk.
- `Pages/` — `HowItEnded`, `HowToReadTheDiff`, `HowToReadTheRoute`, `HowToReadIntake` —
  the board's judgement, where two states distinguished only by wording are tested both
  ways: an empty diff that means "changed nothing" against one that means "did not
  finish", and three intake states where an empty lane must not read as a project with
  nothing to do.

It is fast enough that nobody has a reason not to run it, and **it is the only layer
that runs with no Temporal server and no Docker at all** — measured, with the container
stopped: 94 tests in under a second. It is how "run the fast tests" becomes a thing a
person can type: `dotnet test tests/agent-factory.unit-tests`.

### Architecture — the shape of the projects

`Failures/ArchitectureTests`, over the project graph and the rules above, added beside
the assembly-wide reflection scans rather than in place of them — the scans say what may
call what, the graph says what may name what, and only the graph is something the
compiler can be made to enforce. Decided in
[#61](https://github.com/NaniSoft/agent-factory/issues/61).

### The harness — the real composition

`tests/agent-factory.tests`, the real factory started in-process with `IClock`,
`INOpenCode` and `IGitHub` substituted and everything else real. It is what makes
"nothing waits, defers or times out anywhere in the policy" a fact about the whole
assembly rather than about one project, and it is **end-to-end by construction and that
is its value**: persistence, the HTTP surface, the heartbeat, retries across steps and
the board are all only true of the real composition. Its tests are not moved down a
layer, and a unit test does not re-decide through a host what it can decide without one.

### Why the unit layer is its own project, and why it sits under `tests/`

Three things justify the separate project, and the first is not tidiness:

- **The harness cannot run without Temporal.** `Durability/MinimalEnvTest` is a plain
  `[Fact]`, so on a machine with no `temporal server` it *fails* rather than skipping. A
  project over `Domain` and `Application` runs anywhere, always.
- **It makes the layering structural rather than a convention.** A unit test *cannot*
  reach a host, because the project it references does not contain one — no `Web`, no
  `Infrastructure`, no Temporal, no SQLite. The reference graph is the enforcement.
- **It is how the fast layer becomes a command** rather than an aspiration.

It sits under **`tests/`**, beside `agent-factory.tests`, because the solution keeps one
`src/` for the projects and one `tests/` for the test projects. `src/` is the factory and
its host, and `ArchitectureTests.TheFactoryProjects` asserts the set of *factory* projects
in it is exactly the four — the AppHost is skipped by name. Both test projects are in the
root `agent-factory.slnx`, so `dotnet build agent-factory.slnx` builds and verifies them
like everything else. `Application` grants it `InternalsVisibleTo`, because `Pages/` is
`internal` — the `Api/` records are the process's public surface and the judgement behind
them is not — and the judgement is exactly what this layer tests. A compile-time
visibility change on one assembly is not a surface.

**What already existed under another name stays where it is and is not duplicated.**
`Results/RoundResultDeriverTests.cs` and `Routing/RoutingReaderTests.cs` are already
hermetic and file-based, and are nearly the unit layer already — but the components they
test, `RoundResultDeriver` and `RoutingReader`, live in `Infrastructure`, which the unit
project deliberately cannot reference. The same is true of the merge refusals: they are
exercised end to end in `GitHub/MergeTests.cs` against the real client and real git, and
the unit tests above re-state them at the policy layer because *that* is the layer the
restructure made constructible.

## What replaces the eighteen reflection tests

`PolicyTests` holds eighteen structural pins, sixteen of them IL scans. **The four-project
split does not replace them, and none of them was deleted.** All eighteen are claims about
a method's callers, a record's construction sites, a type's constructor shape or a
vocabulary that is closed — and **none of those is a claim about which project a type sits
in.** A project reference can make an outward dependency impossible to compile; it cannot
say who may call `Orchestrator.AskForTheRound`, may construct a `ProcessRequest`, may read
`RoundResult.IsRetryable` or may hand the deriver an `ILogger<T>` and nothing else. Two of
them get *stronger* rather than redundant: the rule that the review surface may not reach
GitHub, and the rule that `RoundResultDeriver`'s constructor is a logger and nothing
else, both live on the far side of a boundary that now permits an adapter to hold a
transport.

What a split *does* break is the other half: a scan pointed at one assembly stops seeing a
call that moved, its expected list gets one entry shorter, and `Assert.Equal` is still
comparing two lists. That is the silent failure, and the whole of the defence is the scan
set. `Boundary/TheFactory.cs` names the four assemblies and `PolicyTests.CallsMadeBy`
takes them as a set. `ArchitectureTests.The_scan_reads_every_assembly_the_factory_is_made_of`
pins that set and the walk over it — because "the set has four names" and "the walk reads
four assemblies" are different claims, and only the second one catches a scan narrowed back
to one. (When that paragraph was written every line of the factory was still in `Web`, so a
scan handed four assemblies and reading only the first would have passed every pin. The
slices have since moved the code, and the sentence stayed true for the opposite reason: the
lines are spread across the four now, and a scan narrowed back to one *does* see less.)

`ArchitectureTests` also reads the four `csproj` files and asserts the graph, `Domain`'s
total absence of references, and that only `Web` declares the ASP.NET SDK or names an
ASP.NET package. The three libraries cannot see `Microsoft.AspNetCore` at all today, and
that is a property of the graph rather than of a rule — the Web SDK's framework reference
is transitive but flows to a project's *referrers*, and nothing references `Web`.

### What a slice ticket does to its pins

Mechanical, and the same every time:

1. **Move the file, run the suite, and read the failure.** Every pin's expected list is
   written in `Owner().Method()` form, so a method that merely changed project keeps its
   entry verbatim. A pin fails only when the *claim* changed.
2. **A failure of the form "expected one of X, found none" is the real hazard.** It means a
   call left the scanned set, which can only happen if the scan set no longer covers where it
   went. Fix the set, not the list. If a list genuinely must lose an entry, that is a change to
   the policy and belongs in its own ticket.
3. **Two pins will fail on their own and both are correct.** `IssuePublisher` moves to
   `Infrastructure`, so `Orchestrator`'s constructor list and the `IGitHub` caller list
   both have to name a **port** instead of the concrete type — which is rule 1 under
   "The rules a project reference cannot see". Nothing in that pin is a boundary claim;
   it is a collaborator list, and a slice that introduces the port renames the entry.
4. **A library that uses `ILogger<T>` needs a package reference.** The three libraries are
   `Microsoft.NET.Sdk` and see no `Microsoft.Extensions.*` and no ASP.NET — verified, not
   assumed. `Application` and `Infrastructure` both take
   `Microsoft.Extensions.Logging.Abstractions` when they first need it; `Domain` takes
   nothing, ever, and `ArchitectureTests` says so. The durable driver added three more:
   `Microsoft.Extensions.Hosting.Abstractions` for `TemporalTickets`, the
   `BackgroundService` that hosts the worker, and
   `Microsoft.Extensions.DependencyInjection.Abstractions` for resolving the store, the
   policy core and the logger — both in `Infrastructure`; and the `Temporalio` reference
   the workflow and its activities need, also in `Infrastructure`. Each is recorded beside
   the reference it justifies, and **none of them is in `Application`** — see
   "`ITicketActivities` is not a port" above.
5. **Do not touch `PolicyTests`' scanner.** If a fifth project appears,
   `The_scan_reads_every_assembly_the_factory_is_made_of` fails and that failure is the
   answer: add the name to `TheFactory`, then decide deliberately whether the graph test
   in `ArchitectureTests` needs a new edge.
   **And #63 is the counter-example that proves the rule is about the *assembly set* rather
   than about counting to five.** A fifth project did appear — the Aspire AppHost — and it
   was **not** added to `TheFactory`, because the four names are "the assemblies the factory
   is made of" and the AppHost is the host: it holds no policy, names no factory type but the
   one it launches, and every pin in `PolicyTests` is about what the factory may do. What
   keeps that honest rather than convenient is where it was put: outside `src/`, so
   `ArchitectureTests.TheFactoryProjects()`'s four-name assertion and the
   `AgentFactory.*.dll`-beside-the-tests check both stayed true and neither was edited. A
   fifth assembly *of the factory's own* is still a failure of that test, and the answer is
   still to add it.

## Hosting

**The factory is four projects and one process, and it is hosted by a fifth project that is
not part of the factory.** Decided in
[#63](https://github.com/NaniSoft/agent-factory/issues/63); researched in
`docs/research/aspire.md` and the parts of that research this build disagrees with are
called out below.

### The AppHost, and why it is not one of the factory's four

`src/AgentFactory.AppHost` is an Aspire AppHost: `Aspire.AppHost.Sdk`, **pinned at
13.6.1** in its `<Project Sdk="…">` attribute, which is the only place the version lives
because an `Sdk=` attribute is invisible to `dotnet add package`. That version is a
decision rather than a default: Aspire ships a minor a month and each minor ends support of
the previous one, so every 9.x release has been unsupported since 2025-11-11. The SDK
implicitly adds `Aspire.Hosting.AppHost` at the same number, and the runtime version check
reads the *SDK* number, so the two must move together. One warning is suppressed there
(`ASPIRE010`, the Aspire CLI bundle) with its argument in the project file, and one
transitive package is lifted to its patched release (`MessagePack` 2.5.303, for
`GHSA-qhrr-8q5h-9q3h`) with its argument in `Directory.Packages.props`.

**It is not one of the factory's four, and it is under `src/` all the same.** The four are
what the dependency rule above draws, what `ArchitectureTests` asserts the edges of, and
what `Boundary/TheFactory` names as the four assemblies every structural pin in
`PolicyTests` reads. The AppHost names no factory type but the one it launches, holds no
policy, and is the outermost thing in the repository. It sits in `src/` beside them
because this repository keeps one source folder and one test folder at the root — the
layout a solution generated with the Aspire AppHost selected has — and
`ArchitectureTests.TheFactoryProjects` skips it **by name**, so it stays a fifth *project*
and never becomes a fifth assembly inside a scan or a fifth edge in the graph.
**`TheFactory`'s scan set is untouched.** The AppHost is in the root `agent-factory.slnx`
with `DefaultStartup="true"`, so an IDE that opens the solution runs it — the same
mechanism the Aspire templates use — and `dotnet build agent-factory.slnx` builds and
verifies it like everything else.

### The resource graph

Two resources, and the graph is the whole of `src/AgentFactory.AppHost/Program.cs`:

| Resource | What it is | Why |
| --- | --- | --- |
| `temporal` | `temporalio/server:1.28.0` as an Aspire container, entrypoint overridden to `temporal server start-dev`, endpoints `grpc` (7233) and `ui` (8233) | The AppHost owns the durable server's lifetime — that is what "orchestrates the Web app and Temporal" means. Image, tag, command and ports are `docker-compose.yml`'s temporal service, kept in step deliberately: that file is the other way to get a server, the way `dotnet test`'s durable tests and a bare `dotnet run --project src/AgentFactory.Web` use. DCP assigns the container's host ports, so an AppHost run and a compose run coexist. |
| `agent-factory` | `AddProject<Projects.AgentFactory_Web>`, which is `src/AgentFactory.Web` as a project reference rather than a path | The whole factory, launched as its own process. `Program` reads `FactorySettings` and calls `FactoryApp.Create` — the AppHost never composes it. |

**The one environment variable is Temporal's address, and its form is load-bearing.**
`Factory__Temporal__Endpoint` is the factory's own `Factory:Temporal:Endpoint` key, and it
ends up in `Temporalio`'s `TargetHost`. `TargetHost` **rejects a URI** —
`http://localhost:7233` throws `ArgumentException: TargetHost cannot have ://` — so the
wiring is the endpoint's `HostAndPort` (`localhost:56898` under a run of this AppHost) and
not the endpoint itself. Handing over the endpoint's URL, which is the obvious wiring, is a
factory that cannot start its worker; measured, not assumed.

**The board is not proxied, and that line is what makes the graph work at all.** DCP puts a
proxy in front of a project resource by default: the declared host port belongs to the
proxy and the project is told to listen elsewhere. The factory does not obey that
instruction — `FactoryApp.Create` calls `UseUrls(options.BoardUrl)`, and **`UseUrls` beats
`ASPNETCORE_URLS`** (measured; the two documented-possible outcomes of that precedence were
"the app binds 5000 and the proxy cannot get it" and "the proxy binds 5000 and the app binds
a random port", and the second is what happens). So the first run of this AppHost had the
proxy holding 5000 with a dead web process behind it, and `http://127.0.0.1:5000/api/board`
timed out. `WithEndpoint("http", endpoint => endpoint.IsProxied = false)` is the fix, and it
is also the right answer rather than merely the working one: loopback is what authorises
the board, and a hop in front of the listener is a hop that argument never accounted for.

### What the AppHost owns, and what Web owns

**What was decided in [#81](https://github.com/NaniSoft/agent-factory/issues/81) is now
true, with one addition.** The AppHost sees this project's `Program` and its
`FactoryApp.Create(WebApplicationBuilder, FactoryOptions, bool)`, and nothing else. Web owns
the process's composition — the two things a host is told before it is built, **the
telemetry pipeline**, the three registration modules under
`src/AgentFactory.Web/Composition/`, the eight route templates of the JSON surface, the
Board export served at the root beside them, and the API's own document. The AppHost owns
the lifetime, the orchestration of the two processes, the dashboard, and the OTLP endpoint
it hands over. **Nothing in Web needs to know that an AppHost exists**, and nothing in Web
does: the pipeline is registered from the process's own configuration, which the AppHost
happens to fill in.

The addition is that **the telemetry pipeline is `Web`'s and is not the Aspire template's**.
`Composition/ServiceDefaults` is deliberately not `AddServiceDefaults()`; the template's
four calls become one and a half here, and each exclusion has its argument at the code:

- **`ConfigureOpenTelemetry`** — kept: the OTel providers, ASP.NET Core / HttpClient /
  Runtime instrumentation, and **`AddMeter("AgentFactory")`**. That last one is the line the
  factory cannot do without. An OTel `MeterProvider` does not implement `IMeterFactory` — it
  collects through its own listener and filters by *meter name* — so `FactoryMetrics` is
  untouched (its constructor is still exactly `["IMeterFactory", "Meter"]`, as `PolicyTests`
  pins) and an instrument on a meter the provider was not told about is dropped with no
  error at all. `Observability/TelemetryPipelineTests` observes a real measurement through
  the production pipeline, with a control proving the assertion can see an absence.
- **`AddStandardResilienceHandler`** — **removed, and its package is not referenced.** This
  is the most dangerous thing in the ticket. `ConfigureHttpClientDefaults` applies to every
  named client, including the GitHub one: the handler's *attempt* timeout is 10 seconds
  where `GitHubClient.RequestTimeout` is 30, it signals by throwing
  `TimeoutRejectedException` (not a `TimeoutException`, not an `HttpRequestException`, so
  `GitHubClient.AskAsync` catches neither and the exception escapes the seam unclassified),
  it retries non-idempotent calls — merge, pull-request create, issue close — by default,
  and those retries are a second retry policy stacked on `Orchestrator.Approve`'s. Two pins
  hold it, and both were proved to fire by installing the handler and watching them fail:
  `ArchitectureTests.No_project_takes_the_http_resilience_package` (the compile-time half)
  and `Boundary/TheProcessTests.The_github_clients_pipeline_carries_no_second_retry_policy`,
  which walks the handler chain the running process really builds.
- **`AddServiceDiscovery`** — removed: nothing in this factory names a service. GitHub is an
  absolute URL a project file states and Temporal arrives as `host:port`.
- **`MapDefaultEndpoints`** — not called: it maps `/health` and `/alive` *only* in
  Development, which is the environment the test host and every `dotnet run` use, so it is
  two new anonymous inbound routes on the listener loopback is the authorisation for, and it
  is a route table pinned at exactly eight templates.

### Telemetry always on, precisely

The SDK and the instrumentation are registered unconditionally; the **exporter** is
registered when the process has been told where to send, through
`OTEL_EXPORTER_OTLP_ENDPOINT` — Aspire's own design, and the property that keeps `dotnet
test` hermetic, since `dotnet test` starts the real factory several hundred times and none
of those runs has a collector. Under the AppHost the variable is always set. Measured from a
running AppHost rather than taken from the docs, which are internally inconsistent about
this: the factory process is handed `OTEL_EXPORTER_OTLP_ENDPOINT=https://localhost:56897`
(HTTPS, a dynamically allocated port — not 4317/4318, not 18889/18890),
`OTEL_EXPORTER_OTLP_PROTOCOL=grpc`, `OTEL_SERVICE_NAME=agent-factory`,
`OTEL_RESOURCE_ATTRIBUTES=service.instance.id=…`, the dashboard's API key in
`OTEL_EXPORTER_OTLP_HEADERS`, and `ASPNETCORE_URLS=http://127.0.0.1:5000`. So "always on" is
a property of the AppHost: it always supplies a destination, and the factory exports
whenever one is supplied. The OTel *logging* provider is registered under the same
condition, and for a second reason: it is another `ILoggerProvider`, and a host with more
than one is a host whose providers are not handed the external scope that
`Observability/RecordedLog` reads.

### The retired property, and what replaced it

**The factory opens sockets now.** The dashboard is a real listener (another process's), and
the OTLP exporter is a real outbound client, so the "this process opens no socket of any
kind" claim is retired **here, deliberately, on the record**. Three comments said it and all
three were rewritten rather than deleted quietly: `FactoryMetrics`' class remarks, the
counters' comment in `Composition/TheSeams`, and
`PolicyTests.The_observability_component_can_reach_nothing_the_factory_does_not_already_reach`.

**The assertions did not change, because they were always narrower than the comment.**
`PolicyTests`' two `Assert.Empty` scans over the factory's four assemblies still hold and
still mean something: no line in the factory's own code constructs an `HttpListener` or a
`TcpListener`. That is the narrower form the property survives in — Kestrel's listener is
the framework's, the dashboard's belongs to another process, and the exporter is a client —
and it is exactly as strong as it is asked to be. **The Board itself stays bound to
loopback**, which `Boundary/BoardPortTests.One_process_serves_the_board_on_port_5000`
asserts, and the AppHost made that *more* true rather than less by not proxying it.
`TheProcessTests.The_process_holds_exactly_one_http_client_and_the_merger_is_it` also
survives unchanged: the OTel exporter's `HttpClient` is the SDK's, inside the SDK's
assembly, and the claim is about components in the factory's four.

### What the research left open

Six things it could not verify, and where each stands after this ticket:

| Open question | Status |
| --- | --- |
| `UseUrls` vs `ASPNETCORE_URLS` precedence | **Checked.** `UseUrls` wins; a probe that sets both binds the `UseUrls` address. |
| Does an unreachable OTLP endpoint throw into the application? | **Not checked.** No collector was made to disappear while the factory ran. The exporter is registered only when a destination is configured, so what remains unknown is the behaviour of a *configured but unreachable* endpoint. |
| Which OTLP port the AppHost injects | **Checked.** HTTPS on a dynamically allocated port, with the API key in `OTEL_EXPORTER_OTLP_HEADERS`; measured off a running resource. |
| Does the Aspire CLI need a container runtime? | **Not checked.** This machine has Docker, and the graph uses it for Temporal, so there is no container-runtime-free case here to measure. |
| Does this repository have `Restore`-attached MSBuild targets? | **Checked.** None: the only custom target in the repository hangs off `AssignTargetPaths` for the Board export, and Aspire's coordinated restore is unaffected. |
| Temporal dev-server readiness under `WaitFor` | **Not checked.** `WaitFor(temporal)` is in the graph and the container comes up; what was not established is what readiness signal the wait is actually gated on for a gRPC endpoint with no HTTP health check. |

### `AgentFactory.Web`'s namespace

**`AgentFactory.Web`'s root namespace stays `AgentFactory`, and #81 owns that rather than
inheriting it.** The convention the slices established is namespace = project name, and
the entry point is the one thing that convention cannot describe: `Program`'s top-level
statements are compiled into the global namespace and cannot be given one, so a rename
would spread the project's own names across three (`AgentFactory`, `AgentFactory.OpenApi`
and the global `Program`); and `FactoryApp`/`BoardExport` are the *process's* names, not a
slice's. Everything a slice moved out of this project has a namespace that says where it
went.