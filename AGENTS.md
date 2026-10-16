# AGENTS.md

Guidance for agents operating in this repository.

## What this repository is

`agent-factory` takes a repository's GitHub issues and turns them into reviewed pull
requests: one issue in, one approved pull request out, with a human judging every build
in between. It is the implementation of the design in [`DESIGN.md`](DESIGN.md).

The public, reader-facing version is published separately as the Nexus documentation at
<https://nexus.nanisoft.com/docs>. That is a publication target, not a build dependency:
this repository is the implementation of record.

## Commands

.NET 10. Every command runs from the repository root.

```bash
dotnet build src/agent-factory.slnx      # build the factory and its tests
dotnet test  src/agent-factory.slnx      # run the whole suite

dotnet test  agent-factory.unit-tests    # the fast layer alone: no Temporal, no Docker

dotnet run --project src/AgentFactory.Web        # the factory alone; the board is on :5000
dotnet run --project apphost/AgentFactory.AppHost # the factory under Aspire, with telemetry
```

The board is at <http://127.0.0.1:5000>. It is bound to loopback on purpose: the machine
is the trust boundary. The AppHost does **not** proxy it — a proxy would take the port and
leave the process unable to bind it, and it would add a hop that loopback-as-authorisation
never accounted for.

The durable half needs a Temporal server. Either `docker compose up temporal`, or the
AppHost's own `temporal` resource.

```bash
dotnet tool restore                      # `dotnet-ef` is a local tool
cd admin && pnpm install                 # the Board
cd admin && pnpm build                   # Next static export into admin/out
cd admin && pnpm check                   # the Prism consumer gate kit
cd admin && pnpm check:openapi           # regenerate the Board's types and diff them
```

## The architecture

**Read [`docs/architecture.md`](docs/architecture.md).** It is the architecture of record
and this file deliberately does not restate it — the ADRs it replaced, and the previous
version of this file, each restated it, and all three drifted out of step with the code.
One copy is the point.

In one paragraph: `AgentFactory.Domain` → `.Application` → `.Infrastructure` →
`.Web`, dependencies inward only, `Domain` referencing nothing at all. The factory is
four projects and one process, hosted by an Aspire AppHost in `apphost/`. Ports are
declared in `Application` and implemented in `Infrastructure`. A test asserts that
`Application` may name logging and hosting *abstractions* and nothing infrastructural.

The AppHost is a fifth *project* but not a fifth assembly the factory is made of: the
structural scans read the four, and adding the AppHost to them would be wrong.

## The test suite

Two projects. `agent-factory.unit-tests/` is the fast layer and references only
`Domain` and `Application`; `src/agent-factory.tests` is everything whose truth depends
on the real composition. Which layer a test belongs to is decided by **what its truth
depends on**, not by what it looks like — **`docs/architecture.md`'s Testing section**
states it in full. The layers:

- **Unit** (`agent-factory.unit-tests/`) — a rule of `Domain` or `Application` decidable
  without a host, a clock, a database, a socket or a process: `MergePolicy` with a fake
  `IMerger` and a fake `IWorkItemStore`, `BranchName` and `PullRequestText`, the closed
  vocabularies, `Failures.Classify` and `RoundResult.IsRetryable`, `GitDiff` and
  `DiffDocument`, and the `Pages/` judgement. `dotnet test agent-factory.unit-tests` runs
  it alone in under a second, **with no Temporal server and no Docker** — and it cannot
  reach a host, because the project it references does not contain one.
- **Architecture tests** (`Failures/ArchitectureTests.cs`) read the csproj graph and
  assert the reference edges, that `Domain` points nowhere, that only `Web` takes the
  ASP.NET SDK, and that `Application` names no infrastructure. Each has a positive
  control, because **a check that never fires looks exactly like a check that is not
  there** — that lesson is why several guards in this suite assert that they can fail.
- **The policy pins** (`Failures/PolicyTests.cs`) are ~18 claims about *callers*,
  *construction sites*, *constructor shapes* and *closed vocabularies* — "exactly one
  place reads a round's own exit code", "exactly one `Task.Delay` in the assembly",
  "thirteen call sites name a work item". They scan IL across `TheFactory.Assemblies`.
  A project split made some of them mechanically breakable, which is why the scan set is
  named in `Boundary/TheFactory.cs` and pinned by its own test: a scan silently narrowed
  back to one assembly would still pass, and that is the failure mode they exist to stop.
- **The in-process harness** (`Boundary/FactoryHost.cs`) starts the real factory with
  `drivingTheMachine: false` and substitutes `IClock`, `INOpenCode` and `IGitHub`. Most
  of the suite is this, and it is hermetic: no Docker, no network, no sleeps.

**What already existed under another name is left where it is and is not duplicated.**
`Results/RoundResultDeriverTests.cs` and `Routing/RoutingReaderTests.cs` are already
hermetic and file-based, and are nearly the unit layer already — but the components they
test, `RoundResultDeriver` and `RoutingReader`, live in `Infrastructure`, which the unit
project deliberately cannot reference. The merge refusals are likewise exercised end to
end in `GitHub/MergeTests.cs` against the real client and real git; the unit tests
re-state them at the policy layer because that is the layer the restructure made
constructible.

**`docker compose up temporal` or the AppHost starts the durable server.** `TemporalFactAttribute`
probes `localhost:7233` once and skips with the reason when nothing answers. Note the
exception: `Durability/MinimalEnvTest.cs` is a plain `[Fact]`, so on a machine with no
Temporal it **fails** rather than skipping.

The suite count moves: **407 in `agent-factory.tests` and 94 in
`agent-factory.unit-tests`** at the time of writing, with Temporal answering. Treat a
number *lower than the last one* as a failure to explain, never as a cleanup.

## Deliberate, load-bearing oddities

Each of these is asserted somewhere, so read the assertion before changing the thing.

- **Exactly one `Task.Delay` in the assembly**, in `FactoryDriver.ExecuteAsync`, counted
  and named. Nothing else waits, defers or times out: every backoff is a `TimeSpan`
  compared against `IClock`. A second one fails the suite.
- **Exactly one `HttpClient` the factory's own assemblies construct** (`GitHubClient`).
  The OpenTelemetry exporter's is the SDK's.
- **The standard HTTP resilience handler is excluded by construction.** No project
  references `Microsoft.Extensions.Http.Resilience`, so `AddStandardResilienceHandler` is
  not a name that compiles. If it landed on the GitHub client it would put a 10-second
  attempt timeout on it — throwing `TimeoutRejectedException`, a type the client does not
  catch, so it would escape the seam **unclassified** — while also retrying non-idempotent
  merges. Two tests hold this, one by package absence and one by walking the live handler
  chain.
- **The process now opens sockets, deliberately.** The AppHost's dashboard and the OTLP
  exporter both connect out. This retired a property the codebase used to hold — "the
  process opens no socket of any kind" — and the narrower thing that survives is asserted
  instead: no line in the factory's own four assemblies constructs an `HttpListener` or a
  `TcpListener`, and the board stays on loopback.
- **`FactoryMetrics` needs `AddMeter("AgentFactory")`.** Without it the factory's three
  instruments are **silently dropped, with no error**. A test observes an instrument
  arriving through the real pipeline and, as a control, that nothing arrives without it.

## Credentials

A credential **name** lives in a project file; the **value** lives in the environment or in
`secrets/<NAME>`. `ICredentialReader` is the only thing that turns one into the other, and
it has exactly two callers, both asserted.

**The LLM key goes into a round; the GitHub write token never does.** The GitHub token is
resolved on the host, after the container-less round is over, and is used in exactly two
places — an `Authorization` header and the environment of the one `git push` child — and
logged in neither. A test checks the value against every formatted log line, every failure
message and every request's path and body.

## Data, projects and secrets

- **`factories/*.yaml` is the served set**, one file per project, five fields. Adding a
  project needs no rebuild, but configuration loads at start, so it needs a restart.
- **This repository commits no project file.** A committed file is not a sample — it is a
  repository the factory polls, builds from and merges into, given a credential. The
  examples are in `factories/README.md`'s neighbours, outside the loader's directory.
- **`data/` is gitignored** and holds the SQLite store of record, the lifted round trees
  and the logs. **Lifted trees are never cleaned up**: a rejected work item has no other
  copy of its change anywhere, and one process's disk is not yet a problem worth solving
  by losing data.
- **EF Core migrations run at start.** There is no separate migration step and no
  `--idempotent` script, because SQLite has none.
- **A repository with no commits is not buildable, on purpose** — a round diffs against
  the commit it started from. The first commit is a human's.

## Documentation

| | |
| --- | --- |
| [`docs/architecture.md`](docs/architecture.md) | the architecture of record |
| [`DESIGN.md`](DESIGN.md) | what the factory is and does |
| [`SPEC.md`](SPEC.md) | the deployment and product spec |
| [`CONTEXT.md`](CONTEXT.md) | the ubiquitous language. Its *avoid* lists are binding |
| [`docs/agents/`](docs/agents) | issue tracker, triage labels, domain docs |
| [`docs/research/`](docs/research) | throwaway findings, on their own branches |

The ADRs were deleted rather than rewritten: sixteen of them described a factory that ran
rounds in Docker containers, and their boundaries are now enforced by the compiler instead
of by prose. What replaced them is `docs/architecture.md` and the tests.

## Language

[`CONTEXT.md`](CONTEXT.md) is the glossary and it is binding. A **Work item** is not a
ticket, a **Round** is not an iteration or an attempt, the **Board** is not an admin, and
the AI a round drives is the **agent** — never a worker, never a model. `SPEC.md` and
`DESIGN.md` use the same words and must keep using them.
