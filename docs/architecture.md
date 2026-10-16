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

All seven interfaces are **declared in `Application`** and **implemented in
`Infrastructure`**. That is the whole of Clean Architecture's outward rule: the
inside says what it needs, the outside says how it gets it.

| Port | Declared | Implemented by |
| --- | --- | --- |
| `IClock` | `Application` | `SystemClock`, `TestClock` |
| `ICredentialReader` | `Application` | `CompositeCredentialReader` |
| `IProcessCli` | `Application` | `ProcessCli`, `FakeProcessCli` |
| `INOpenCode` | `Application` | `RoundRunner`, `FakeNOpenCode` |
| `IGitHub` | `Application` | `GitHubClient`, `FakeGitHub` |
| `IWorkItemStore` | `Application` | `SqliteWorkItemStore` |
| `ITicketStarter` | `Application` | `TemporalTickets`, `NullTicketStarter` |

**Seven, and there used to be an eighth.** `ITicketActivities` was listed here and is not a
port. It lives in `Infrastructure` beside `TicketWorkflow`, its only caller, and
`Application` no longer names `Temporalio` at all — the argument is in
"`ITicketActivities` is not a port" below, because it is a correction rather than a
missing row.

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
- `Observability/FactoryMetrics` → **`Application`.** It is four instruments on the
  platform's own `System.Diagnostics.Metrics`, and `Orchestrator` and `Poller` both take
  it as a concrete type — so putting it in `Infrastructure` would make `Application`
  depend on `Infrastructure`, which is the one cardinal violation this rule has. What
  belongs in `Infrastructure` is the **OpenTelemetry exporter registration**, which is
  what turns instruments into signals and is [#63](#63)'s, not this file's. **This
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
  because the word names the decision rather than the library — ADR-0009 says the record of
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

Policy is still tested the way it always was: the real factory, started in-process,
with `IClock`, `INOpenCode` and `IGitHub` substituted and everything else real. That
harness survives the split as the integration layer, because it is what makes
"nothing waits, defers or times out anywhere in the policy" a fact about the whole
assembly rather than about one project.

Around it, two layers are new:

- **Unit tests** over `Domain` and `Application`, which can now be constructed
  directly rather than reached through a host. This is the "hard to test in
  isolation" the restructure is for.
- **Architecture tests** over the project graph and the rules above, added beside the
  assembly-wide reflection scans rather than in place of them — the scans say what may
  call what, the graph says what may name what, and only the graph is something the
  compiler can be made to enforce. Decided in
  [#61](https://github.com/NaniSoft/agent-factory/issues/61).

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

## Hosting

The factory is four projects and one process. A .NET Aspire AppHost composes the
web application and Temporal, and carries telemetry. Decided in
[#63](https://github.com/NaniSoft/agent-factory/issues/63); the shape is in
`docs/research/aspire.md`.

**What the AppHost will see, and what Web owns, were decided in
[#81](https://github.com/NaniSoft/agent-factory/issues/81).** It sees this project's
`Program` and its `FactoryApp.Create(WebApplicationBuilder, FactoryOptions, bool)`, and
nothing else. Web owns the process's composition — the two things a host is told before it
is built, the three registration modules under `src/AgentFactory.Web/Composition/`, the
eight route templates of the JSON surface, the Board export served at the root beside them,
and the API's own document — and the AppHost will own the lifetime, the orchestration of
the two processes, and the telemetry exporter. Nothing in Web may need to know that an
AppHost exists.

**`AgentFactory.Web`'s root namespace stays `AgentFactory`, and #81 owns that rather than
inheriting it.** The convention the slices established is namespace = project name, and
the entry point is the one thing that convention cannot describe: `Program`'s top-level
statements are compiled into the global namespace and cannot be given one, so a rename
would spread the project's own names across three (`AgentFactory`, `AgentFactory.OpenApi`
and the global `Program`); and `FactoryApp`/`BoardExport` are the *process's* names, not a
slice's. Everything a slice moved out of this project has a namespace that says where it
went.

One consequence is already decided and is not a discovery: **the factory now
opens sockets.** The Aspire dashboard and an OpenTelemetry exporter both listen,
which retires the "this process opens no socket of any kind" property deliberately.
The Board itself stays bound to loopback.