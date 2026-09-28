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
`GitHub/` (one seam covering both polling and merging).

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
moves work items Backlog → Frontier → In Progress → Review, and it is the whole of the
factory's policy. It references no container runtime: asking `INOpenCode` for a round and
receiving a result is all it knows, so the agent is faked in tests and no Docker appears.

One `Step()` applies at most one transition and never waits, which is what makes the
90-minute `FactoryConstants.RoundTimeout` a comparison against `IClock` rather than a
timer. Like the poller it is stepped rather than driven, and like the poller nothing
steps it in production: there is no real `INOpenCode` to step against, and a background
loop that could never run a round is noise. Whoever adds the real agent owns the
heartbeat that steps both.

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
