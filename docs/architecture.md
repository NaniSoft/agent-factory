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
- `Observability/WorkItemScope`, `Observability/ProjectScope` → **`Application`.**
  The scope vocabulary is the factory's own ("this record is about work item 4,
  project nexus, issue 42, round 2") and a record is written from inside.
- `Observability/FactoryMetrics` → **`Infrastructure`.** It is the wiring that turns
  instruments into signals, and it is where an OpenTelemetry exporter will live.

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
4. **`Observability/FactoryMetrics` depends on `Rounds`.** Resolved by the split
   above — the instruments describe outcomes, which are `Domain`'s.

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
- **Architecture tests** over the project graph and the rules above, replacing the
  assembly-wide reflection scans that a four-project split breaks mechanically.
  Decided in
  [#61](https://github.com/NaniSoft/agent-factory/issues/61).

## Hosting

The factory is four projects and one process. A .NET Aspire AppHost composes the
web application and Temporal, and carries telemetry. Decided in
[#63](https://github.com/NaniSoft/agent-factory/issues/63); the shape is in
`docs/research/aspire.md`.

One consequence is already decided and is not a discovery: **the factory now
opens sockets.** The Aspire dashboard and an OpenTelemetry exporter both listen,
which retires the "this process opens no socket of any kind" property deliberately.
The Board itself stays bound to loopback.