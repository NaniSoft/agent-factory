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
         AgentFactory.Application     ← Orchestrator, Poller, FactoryDriver, all eight ports,
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

All eight interfaces are **declared in `Application`** and **implemented in
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
| `ITicketActivities` | `Application` | `TicketActivities` |

`ITicketActivities` is the one that reads backwards, and deliberately so: it is an
**inbound** port. The durable workflow lives outside and drives the factory through
it, so it is declared inside and implemented by the driver. That is the same shape
as an HTTP endpoint — the caller is outside, the contract is the inside's.

**It is also the one port that drags a framework inward, and that is a correction to
this rule rather than an oversight (#76).** The engine resolves every activity call
from an `[Activity]` attribute on the very method the workflow invokes — the SDK's own
`ActivityDefinition.NameFromMethodForCall` throws without one — so an inbound durable
port cannot be declared without the engine's own vocabulary, and `Application` names
`Temporalio` for this interface alone. Two alternatives were refused: an
attribute-free port does not work at all, and moving the attributes to
`TicketActivities` would make the workflow call the concrete adapter and leave the
port decorative. The same shape already holds for `FactoryDriver`, which is a
`BackgroundService` and is why `Application` names the hosting abstractions: when a
driver *is* the framework's contract, the inside speaks the framework's words.
Nothing in the policy core names a Temporalio type, and the engine's adapters —
`TicketActivities`, `TicketWorkflow`, `TemporalTickets`, `NullTicketStarter` — are in
`Infrastructure`.

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
  **`Application`**, beside `IWorkItemStore` rather than beside `SqliteWorkItemStore`.
  They are the shapes rows take, so they read as persistence shape and the first
  guess is `Infrastructure` — and `Infrastructure` is where the thing that maps
  them to columns is, so the guess looks right. It is not available: a port's
  signatures name these types, and a signature cannot name a type that lives
  beyond it. What they are is the **port's** vocabulary — what the inside says a
  round, a decision and a pending round are — which is something a caller needs
  in order to call the port at all. No field of any of them is a column: no
  attributes, and every type is the BCL's or the factory's own. The one place
  the two halves touch is `SqliteWorkItemStore` itself, which is still in `Web`
  and is the thing this map's store slice moves.
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
four assemblies" are different claims, and every line of the factory is still in `Web`, so
only the second one catches a scan narrowed back to one.

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
   nothing, ever, and `ArchitectureTests` says so. The durable driver added two more of the
   same kind — `Microsoft.Extensions.Hosting.Abstractions` for the `BackgroundService` and
   `Microsoft.Extensions.DependencyInjection.Abstractions` for resolving the store, the
   policy core and the logger — plus, for `Application`, the `Temporalio` reference the
   inbound port above needs. Each is recorded beside the reference it justifies.
5. **Do not touch `PolicyTests`' scanner.** If a fifth project appears,
   `The_scan_reads_every_assembly_the_factory_is_made_of` fails and that failure is the
   answer: add the name to `TheFactory`, then decide deliberately whether the graph test
   in `ArchitectureTests` needs a new edge.

## Hosting

The factory is four projects and one process. A .NET Aspire AppHost composes the
web application and Temporal, and carries telemetry. Decided in
[#63](https://github.com/NaniSoft/agent-factory/issues/63); the shape is in
`docs/research/aspire.md`.

One consequence is already decided and is not a discovery: **the factory now
opens sockets.** The Aspire dashboard and an OpenTelemetry exporter both listen,
which retires the "this process opens no socket of any kind" property deliberately.
The Board itself stays bound to loopback.