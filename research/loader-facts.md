# Research: What the config loader does to `factories/` today, and what a config UI must fit around

**Ticket:** wayfinder #28 / GitHub issue #28
**Branch:** `research/loader-facts` (from `main` @ `3b0efe6`)
**Date:** 2026-09-30
**Sources:** the code only. All paths relative to the repository root; line numbers are from this commit.

## Question

What does the config loader actually do with `factories/` right now, and what must a config UI fit around?

---

## 1. Watch vs. load-once: `ProjectFileLoader` reads once, at startup, and nothing watches

**Load-once.** There is no `FileSystemWatcher` anywhere in `src/` (a repo-wide search for `FileSystemWatcher|Watch|hot.reload` returns only comments saying there is none).

`ProjectFileLoader.Load` is a static, synchronous directory scan, called from exactly one place — the DI registration in `FactoryApp.Create` — as a singleton:

`src/agent-factory/FactoryApp.cs:119-123`

```csharp
// Loaded once at start, with no hot reload. A project file that fails validation
// takes only its own project out of the rotation and is reported on the board.
builder.Services.AddSingleton(services => ProjectFileLoader.Load(
    options.FactoriesDirectory,
    services.GetRequiredService<ILoggerFactory>().CreateLogger("agent-factory.projects")));
```

and is forced eagerly at composition time, so the read happens before the host serves anything:

`src/agent-factory/FactoryApp.cs:170-172`

```csharp
// Resolved here rather than on the first board render, so configuration is read
// at start the way it is meant to be.
_ = app.Services.GetRequiredService<ProjectLoadReport>();
```

The resulting `ProjectLoadReport` is an **immutable snapshot injected into five consumers** (`GitHubClient`, `Poller`, `WorkerRoundRunner`, `IndexModel`, and the tests' `FactoryHost`). Nobody ever calls `ProjectFileLoader.Load` a second time, and nothing holds a reference to the loader itself.

**What picking up changes would take.** A config UI that edits `factories/*.yaml` would have to either (a) restart the process, or (b) replace the `ProjectLoadReport` singleton — which is not possible without reworking registration, because the report is captured **by value into per-consumer fields at construction**: the poller copies it into a private rotation list in its constructor (`Poller.cs:79`), and `GitHubClient`/`WorkerRoundRunner`/`IndexModel` each hold the record directly (`GitHubClient.cs:149`, `WorkerRoundRunner.cs:106`, `Index.cshtml.cs:33`). There is no indirection, no `IOptionsMonitor`, no re-loadable seam. Three components state the no-hot-reload contract in their failure messages:

- `src/agent-factory/GitHub/GitHubClient.cs:916-923` — "project files load at start and do not hot reload … Project files are read at start, so a change to one means a restart"
- `src/agent-factory/Rounds/WorkerRoundRunner.cs:149-153` — "configuration loads at start and does not hot reload, so nothing about the second attempt would differ from the first"
- `src/agent-factory/Polling/Poller.cs:259-266` — "a permanent failure is only cleared by a restart, because everything a permanent intake failure is about — a project file, the set being served, an environment variable that was not set when the factory started — is read at start and does not hot reload"

Which directory it reads is resolved by `FactoryPaths.ResolveRoot` (`src/agent-factory/FactoryPaths.cs:19-38`): `Factory:Root` config → `FACTORY_ROOT` env var → walk up from the content root looking for a directory named `factories`; the name is the constant `FactoryPaths.FactoriesDirectoryName = "factories"`. `FactoryOptions.FactoriesDirectory` defaults to `<root>/factories` (`FactoryOptions.cs:34`).

Only top-level `*.yaml`/`*.yml` files are read — no subdirectories (`ProjectFileLoader.cs:129-134`).

---

## 2. Validation, and how failures surface: log + board, never a throw; isolation is implemented

The result of a load is `ProjectLoadReport` — the served list plus the refused list, both sorted:

`src/agent-factory/Projects/ProjectLoadReport.cs:36-41`

```csharp
public sealed record ProjectLoadReport(
    IReadOnlyList<Project> Projects,
    IReadOnlyList<ProjectFileRejection> Rejections)
{
    public static readonly ProjectLoadReport Empty = new([], []);
}
```

Rejection reasons, five of them (`ProjectLoadReport.cs:7-23`): `Partial` (a required field missing/empty), `Shared` (project name in two files, a multi-document YAML file, or a YAML merge key), `Included` (`include:`/`extends:`/`import:` key present), `Generated` (`*.generated.yaml`), `Invalid` (unknown field, bad URL, bad credential name, unreadable file, not valid YAML).

**Failures surface in two places, neither silent:**

1. **The log**, at start, once per refusal — `ProjectFileLoader.cs:307-313`:

```csharp
private static void Refuse(List<ProjectFileRejection> refused, ILogger logger, ProjectFileRejection rejection)
{
    refused.Add(rejection);
    logger.LogWarning(
        "Refused project file {File}: {Reason} — {Message}. Its project is out of the rotation",
        rejection.FileName, rejection.Reason, rejection.Message);
}
```

   plus the per-project "Serving project …" lines and the total at `ProjectFileLoader.cs:105-127` (the doc comment there, :87-93, says explicitly that the log is where "did my change to `factories/` take effect" is answered, because the board is "a surface a human has to remember to open").

2. **The board**, as a dedicated section — `src/agent-factory/Pages/Index.cshtml:150-160` renders `Model.Rejections` under the heading "Project files refused", carrying `data-refused-file` and `data-reason` attributes plus the reason message as text; the page model exposes both sets read-only (`Index.cshtml.cs:50-54`):

```csharp
/// <summary>The set the factory is actually serving: exactly the files that validated.</summary>
public IReadOnlyList<Project> Projects => _projects.Projects;

/// <summary>The files that were refused, and why. A refusal is reported, not swallowed.</summary>
public IReadOnlyList<ProjectFileRejection> Rejections => _projects.Rejections;
```

**Yes, the DESIGN.md rule is implemented.** DESIGN.md:60-63 says: "The config loader validates a project file before anything downstream sees it. A file that fails validation keeps its project out of the rotation rather than failing the whole factory later, mid-run, in a worker container." The mechanism is a per-file `try/catch` around a private `ProjectFileRefusedException` (`ProjectFileLoader.cs:47-61`), so a bad file never escapes `Load`; a missing `factories/` directory logs a warning and returns `ProjectLoadReport.Empty` instead of throwing (`ProjectFileLoader.cs:32-38`); a shared project name is removed from the served list rather than rejected outright (`ProjectFileLoader.cs:63-75`). It is asserted by test `A_project_file_that_fails_validation_keeps_its_own_project_out_of_the_rotation` (`src/agent-factory.tests/Projects/ProjectFileTests.cs:54`).

Validation is strict, closed-world — a config UI must reproduce it or it will produce files the loader refuses:

- exactly five top-level fields `name`, `repo`, `worker`, `llm`, `keys`; anything else is refused (`ProjectFileLoader.cs:21`, `:199-203`)
- exactly `repo.url`, `worker.image`, `llm.provider`, `keys.github`, `keys.llm` under those groups (`:25-28`, `:245-268`)
- every field required — no defaults, no optional fields, empty file refused (`:152-156`, `:205-209`)
- `repo.url` must be an absolute http/https URL (`:223-228`)
- both key values must be valid environment-variable names (`:230-240`, `IsEnvironmentVariableName` at `:298-301`)
- the schema doc comment is explicit that "Nothing further is accepted: no prompts, no per-issue overrides, no board configuration, no plugin list" (`ProjectFileLoader.cs:12-18`)
- rotation order is directory order sorted by file name, not declared anywhere (`ProjectFileLoader.cs:77-81`)

---

## 3. The exact shape of `Project` — what a UI form must fill

The whole record, seven strings and nothing else (`src/agent-factory/Projects/Project.cs:7-14`):

```csharp
public sealed record Project(
    string Name,
    string RepoUrl,
    string WorkerImage,
    string LlmProvider,
    string GitHubKeyName,
    string LlmKeyName,
    string SourceFile);
```

`SourceFile` is derived (the file name the loader read), so **a UI form has exactly six inputs**, mapping to this YAML structure — the only accepted shape, shown by the repo's own `factories/nexus.yaml`:

```yaml
name: nexus
repo:
  url: https://github.com/NaniSoft/nexus
worker:
  image: ghcr.io/nanisoft/agent-factory-worker:1
llm:
  provider: anthropic
keys:
  github: NEXUS_GITHUB_TOKEN
  llm: NEXUS_ANTHROPIC_API_KEY
```

Constraints the form must enforce to match the loader: name non-empty; `repo.url` absolute http(s); `keys.*` match `^[A-Za-z_][A-Za-z0-9_]*$`; no unknown keys anywhere; no YAML merge keys or includes; one document per file; file name `.yaml`/`.yml` and not `*.generated.*`; project name unique across the directory (case-insensitively — `ProjectFileLoader.cs:64`). A UI writing a file also has to respect the loader's own rule that a generated project file "is derived by the loader, not written by anyone" (`ProjectFileLoader.cs:49-53`) — i.e. a UI must not use the `.generated.yaml` suffix.

---

## 4. Credential resolution: name → value at the last possible moment, in two places only

`ICredentialReader` is a single method, and the doc comment fixes the boundary (`src/agent-factory/Credentials/ICredentialReader.cs:14-22`):

```csharp
string? Read(string name);
```

The real implementation is the host environment, and it is the only place in the process where a value is picked up (`src/agent-factory/Credentials/ProcessEnvironment.cs:13-16`):

```csharp
public string? Read(string name) =>
    string.IsNullOrWhiteSpace(name)
        ? null
        : Environment.GetEnvironmentVariable(name);
```

Registered as a singleton at `FactoryApp.cs:82` (`TryAddSingleton<ICredentialReader>(ProcessEnvironment.The)`), so a test host substitutes `FakeCredentialReader`. It is read **lazily per round / per request, never at load** — a missing credential is not a load failure.

Where the values actually go:

| Value | Read at | Goes to |
|---|---|---|
| `keys.llm` → LLM key | `WorkerRoundRunner.EnvironmentFor`, `WorkerRoundRunner.cs:411-413` | the worker container's environment, under the project-declared name |
| `keys.github` → GitHub token | `GitHubClient.TokenFor`, `GitHubClient.cs:931-942` | one `Authorization` header per API request, and the `http.extraheader` of the one git child process that pushes (`GitPusher.cs:96-141`) — never a command line, never a URL, **never the worker container** |

`WorkerRoundRunner.cs:388-398` states the structural rule: "The project's `keys.github` name is not read here, anywhere, ever. That is what makes ADR-0006 structural rather than a matter of care." A missing LLM key does not stop the round — it logs a warning and runs anyway (`WorkerRoundRunner.cs:420-433`); a missing GitHub token is a `PermanentFailure` (`GitHubClient.cs:938-941`).

The rest of the worker container's environment is four `AGENT_FACTORY_*` variables derived from the round, not from config (`WorkerRoundRunner.cs:399-409`): `AGENT_FACTORY_REPO_URL`, `AGENT_FACTORY_BASE_REF`, `AGENT_FACTORY_BRIEF`, `AGENT_FACTORY_AGENT_PROMPT`.

---

## 5. Where factory-level (not per-project) settings live

Two places, and both are deliberately not user-facing configuration.

**(a) `FactoryOptions` — the only thing that *is* configuration** (`src/agent-factory/FactoryOptions.cs:8-37`), three values read from the `Factory:` config section:

```csharp
public sealed record FactoryOptions(
    string FactoriesDirectory,
    string DatabasePath,
    Uri BoardUrl)
```

with `Factory:FactoriesDirectory`, `Factory:DatabasePath`, `Factory:BoardUrl` keys (`FactoryOptions.cs:34-36`) plus `Factory:Root`/`FACTORY_ROOT` for the root itself (`FactoryPaths.cs:24-30`). Its doc comment is the boundary statement: "None of it is per project — project files are the only per-project configuration, and there is no other" (`FactoryOptions.cs:4-7`). The checked-in `src/agent-factory/appsettings.json` sets only `Factory:BoardUrl` — no other factory keys exist in any settings file.

**(b) `FactoryConstants` — behaviour is code, never configuration** (`src/agent-factory/FactoryConstants.cs:3-6`): "Factory behaviour is code, never configuration. Every project gets identical behaviour, so these live here and nowhere else." This is where the knobs a config UI might want already live, as constants:

| Constant | Value | Line |
|---|---|---|
| `RoundCeiling` | `3` rounds max per work item | `:10` |
| `FeedbackThreshold` | `TimeSpan.FromHours(48)` — **the auto-merge timer already exists here** | `:26` |
| `RoundTimeout` | 90 minutes | `:40` |
| `TransientRetryAttempts` | 3 | `:62` |
| `RetryBackoffBase` | 10 s, doubling | `:75` |
| `ContainerBudget` | 2 concurrent worker containers | `:119` |
| `HeartbeatInterval` | 5 s | `:134` |
| `PollBackoffCeiling` | 16 minutes | `:150` |
| `PollInterval` | 60 s per intake pass | `:178` |
| `BoardAutoRefreshSeconds` | 5 | `:94` |

So for the ticket's "where would an auto-merge flag naturally sit": auto-merge is not a flag today — it is `FeedbackThreshold`, applied in `Orchestrator.MergeWhatNobodyReviewed` and rendered per work item by `IndexModel.AutoMergeAt` (`Index.cshtml.cs:168-171`, `workItem.ReviewStartedUtc + FactoryConstants.FeedbackThreshold`). Making it configurable would mean moving it out of `FactoryConstants`, whose every member carries a doc comment arguing that "changing it is a deliberate edit in one obvious place" (e.g. `:14-17`, `:44-47`, `:100-102`). The natural seam for factory-level settings is `FactoryOptions` (it is the record already built from `IConfiguration` and injected everywhere); the per-project seam is the project file, whose schema the loader refuses to widen.

---

## 6. How the poller and the orchestrator consume loaded projects — and what happens when a file disappears

**The poller takes a snapshot in its constructor** and never sees the directory again (`Polling/Poller.cs:79`):

```csharp
_rotation = [.. projects.Projects.OrderBy(project => project.SourceFile, StringComparer.OrdinalIgnoreCase)];
```

It is stepped, not timer-driven: one `StepAsync` is one project's turn, a pass is the whole rotation, and a pass is due only after `FactoryConstants.PollInterval` (`Poller.cs:205-206`, doc at `:19-24`). Each project's last outcome is an in-memory `IntakeRecord` (`Poller.cs:62`), exposed to the board as `Poller.Intake` (`Poller.cs:193-194`) and rendered through `HowToReadIntake.Each` in `IndexModel.Intake` (`Index.cshtml.cs:73`). A project that fails **permanently** (repo gone, credential absent, not a repo) is never read again by that process (`Poller.cs:243-325`, log once at `:291-307`) — and the only recovery named is a restart (`:259-266`).

**What happens to a project's work items when its file disappears or turns invalid:**

- **The project's issues are simply never polled again** — the rotation is fixed at construction; nothing deletes or archives anything.
- **Existing work items survive in SQLite** (`IWorkItemStore`) and the board keeps them visible: `Index.cshtml.cs:86-96` builds `ProjectsOnTheBoard` as *served projects plus any project that still has work items*, "A project the factory has stopped serving still has a history a reviewer is judging, and hiding it behind a filter that cannot be selected would lose it."
- **A round already scheduled for it cannot start**: `WorkerRoundRunner.cs:146-162` looks the project up in the loaded report, finds nothing, and returns `RoundResult.Failed(FailureClass.Permanent)` — "That is a round that cannot start, not a round that failed … Retrying it would spend two more containers to be told the same thing."
- **That permanent round result parks the work item in Escalated**, where a human can still merge or reject it (`Loop/Orchestrator.cs:856-863`):

```csharp
// A round that came back with a result is what a reviewer judges, so it goes to
// Review. A round that did not is not merged over: it parks in Escalated, which a
// human can still merge or reject (ADR-0008).
_store.Move(
    run.WorkItemId,
    result.Outcome == RoundOutcome.Produced ? Swimlane.Review : Swimlane.Escalated);
```

- **Merging for an unserved repository is refused outright** — `GitHubClient.ProjectServing` (`GitHubClient.cs:908-924`) throws `PermanentFailure`: "no project file is being served for {repo}, so there is no credential to push with and this factory is not building for that repository. Project files are read at start, so a change to one means a restart."

The orchestrator itself never reads the loader or the directory; it only ever sees a project's name through a work item, and the round runner re-resolves the name against the startup snapshot (`WorkerRoundRunner.cs:146`).

---

## Summary for a config UI

- **It must not expect its edits to take effect.** The loader is a one-shot directory scan at host composition; the report is an immutable snapshot copied into consumers' fields. The only existing "apply" mechanism is process restart — three components say so to operators in their log/board messages.
- **To become re-loadable, the UI work is really a registration rework**: replace the `ProjectLoadReport` singleton with a re-loadable seam (and rebuild `Poller._rotation`), before any editing UI is worth building.
- **Factory-level settings belong in `FactoryOptions`** (the only `IConfiguration`-backed factory record, three keys today) — not in `FactoryConstants`, whose members are code by explicit design. Auto-merge already exists as `FactoryConstants.FeedbackThreshold` (48 h), not as a flag.
- **A project form has six fields** (`name`, `repo.url`, `worker.image`, `llm.provider`, `keys.github`, `keys.llm`) and must validate to the loader's closed-world rules, or the file it writes will be refused — visibly, on the board and in the log.
- **Credential values never enter config.** The loader stores names; `ICredentialReader` (host env) resolves them at round/merge time; the LLM key goes into the worker container, the GitHub token never does.
