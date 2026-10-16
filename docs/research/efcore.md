# EF Core migrations against SQLite on .NET 10

Research for [#56](https://github.com/NaniSoft/agent-factory/issues/56), under the map in
[#54](https://github.com/NaniSoft/agent-factory/issues/54).

**Scope.** Does EF Core 10 migrate cleanly against SQLite *for this store*, and what does a
`DbContext` cost the hand-written schema in
[`src/agent-factory/WorkItems/SqliteWorkItemStore.cs`](../../src/agent-factory/WorkItems/SqliteWorkItemStore.cs)?
The map has already decided: EF Core with migrations, and the live `data/agent-factory.db` is
**not** migrated — a fresh database is accepted.

**How this was established.** Every claim below is either (a) cited to primary
documentation, or (b) **verified by running EF Core 10.0.13 against SQLite on this machine**.
The verification harness was a throwaway console project outside this repository that
reproduces the store's four tables, column for column, and runs the same queries the board
and the loop issue. Raw outputs are quoted inline. Where something could not be verified, it
is marked **not verified** rather than guessed at.

---

## Bottom line

EF Core 10 **does** migrate cleanly against SQLite, and there is **no** SQLite-specific
migration blocker. `DROP COLUMN` is not a problem — EF implements it as a table rebuild.
`ALTER COLUMN` is a rebuild. Both are supported.

But the migration is not the hard part. **The hard part is that this store's two central
read patterns — order the board by when a work item was created, and ask whether a
reviewer has waited long enough — throw `NotSupportedException` the moment the column is
mapped as `DateTimeOffset`.** That is not a SQLite limitation and not a tuning problem; the
provider refuses to translate those expressions at all. It is fixable, deliberately, with a
value converter, and the fix has a cost worth naming before it is chosen.

| | Verdict |
| --- | --- |
| Packages and `dotnet ef` | Settled. Three packages, one version, plus a local tool manifest. |
| SQLite migration constraints | No blocker. One real loss: `--idempotent` scripts are unsupported. |
| `DbContext` vs the existing schema | Maps cleanly. Two behavioural changes (enum storage, foreign keys). |
| Column type mapping | Faithful round-trip. **`ORDER BY` and comparison on `DateTimeOffset` throw.** |
| Fresh-database path | `migrations add Baseline` + `database update`. `EnsureCreated` is the wrong tool here. |
| Read performance | Better than today. Pooling on, WAL on, `AsNoTracking` free. |

---

## 1. Packages, versions, and `dotnet ef`

### What exists today

`src/agent-factory/agent-factory.csproj` references `Microsoft.Data.Sqlite` **10.0.12**,
`Temporalio` 1.20.0 and `YamlDotNet` 18.1.0. There is **no EF Core anywhere in the
repository**, and `dotnet ef` is **not installed** on this machine (`dotnet ef --version`
fails with *"the dotnet-ef does not exist"*).

### What to add

Latest stable on the 10.x line, confirmed against the NuGet flat-container index:

| Package | Version | Notes |
| --- | --- | --- |
| `Microsoft.EntityFrameworkCore.Sqlite` | **10.0.13** | The provider. Brings `…Relational`, `…Sqlite.Core`, `…Analyzers`. |
| `Microsoft.EntityFrameworkCore.Design` | **10.0.13** | Required **only** by `dotnet ef`. `PrivateAssets=all`. |
| `dotnet-ef` (tool) | **10.0.13** | The CLI. |

The three ship in lockstep — all three are at `10.0.13`, and EF's tooling checks that the
tool version matches the runtime version, so they must be bumped together.

> **This repository now uses Central Package Management.** `Directory.Packages.props`
> (untracked, on `wayfinder/clean-architecture` as of this writing) sets
> `ManagePackageVersionsCentrally` and holds every version. So the change is **two
> `PackageVersion` entries there**, plus version-less `PackageReference`s in the csproj —
> not a `Version=` attribute in the csproj.

### Verified: no dependency conflict

A scratch project carrying the factory's **exact current package set** plus EF Core was
restored: *"Restored … (in 872 ms)"* — **no warnings, no conflicts**. `Temporalio`'s
`Google.Protobuf` 3.26.1 and its gRPC stack are untouched by EF.

Two version facts that restore does **not** warn about:

```
Microsoft.Data.Sqlite            10.0.12    <- the direct reference, unchanged
Microsoft.Data.Sqlite.Core       10.0.13    <- transitive via EF  (SPLIT)
SQLitePCLRaw.bundle_e_sqlite3    2.1.13     <- was 2.1.12       (BUMP)
SQLitePCLRaw.core                2.1.13     <- was 2.1.12       (BUMP)
SQLitePCLRaw.lib.e_sqlite3       2.1.13     <- was 2.1.12       (BUMP)
```

1. **A `Microsoft.Data.Sqlite` split.** The direct reference stays at 10.0.12 while
   `Microsoft.Data.Sqlite.Core` resolves to 10.0.13. The assembly actually loaded is the
   10.0.13 one. It works, but it is untidy — bump the direct reference to 10.0.13 in the
   same change.
2. **The bundled native SQLite engine changes.** `SQLitePCLRaw.lib.e_sqlite3` 2.1.12 → 2.1.13
   means the SQLite that ships with the process is not the SQLite that ships with it today.
   This is worth an explicit note in the PR: it is a behaviour change to the database engine
   itself, and `PRAGMA journal_mode`/`foreign_keys` behaviour should be re-checked against
   it rather than assumed.

### `dotnet ef`: local manifest, not global

**Use a local tool manifest.** A global install makes every developer's migration experience
depend on machine state that is not in the repository; the manifest makes the tool version a
committed fact. The repository has **no tool manifest today** (no `.config/`, no
`dotnet-tools.json`).

**One verified gotcha.** The .NET CLI docs state that `dotnet new tool-manifest` *"creates a
manifest file named dotnet-tools.json under the .config directory"* — but on SDK **10.0.401**
it does not. Verified twice, via both `dotnet new tool-manifest` and
`dotnet tool install --local`:

```
FullName
-------
…\efprobe\dotnet-tools.json          <- project root, NOT .config\
```

The generated manifest also carries a key the older format did not:

```json
{ "version": 1, "isRoot": true, "tools": { "dotnet-ef": { "version": "10.0.13", … } } }
```

So: commit the manifest wherever the SDK puts it, and do not hand-place it at `.config/`
expecting the SDK to find it — it works when the SDK generates it, but the documented path
and the actual path differ and a reader will hit that.

`global.json` pins the SDK at `10.0.401` with `rollForward: latestFeature`, which means a
contributor on a different patch band may get a different manifest location. Worth one line
in the PR so the next person is not surprised.

---

## 2. Does SQLite support migrations cleanly on .NET 10?

**Yes. The commonly-cited SQLite migration blockers are not blockers here.**

The provider's own limitations page ([SQLite provider limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations))
is explicit that the operations this store would ever need are supported, and that most are
implemented as a **table rebuild**:

| Operation | Supported? | How |
| --- | --- | --- |
| `CreateTable`, `AddColumn`, `CreateIndex`, `DropTable` | ✔ | direct |
| `DropColumn` | ✔ | **rebuild** |
| `AlterColumn` | ✔ | **rebuild** |
| `AddForeignKey`, `DropForeignKey` | ✔ | rebuild |
| `AddPrimaryKey`, `DropPrimaryKey`, `AddUniqueConstraint`, `DropUniqueConstraint` | ✔ | rebuild |
| `DropCheckConstraint`, `AddCheckConstraint` | ✔ | rebuild |
| `RenameColumn`, `RenameTable` | ✔ | direct |
| `RenameIndex` | ✔ | rebuild |
| `Insert`, `Update`, `Delete` (data) | ✔ | direct |

**On the "SQLite has no `DROP COLUMN`" worry specifically:** that was true of SQLite before
3.35.0. The engine bundled here is **3.53.3** (verified: `select sqlite_version()`), so
native `DROP COLUMN` exists — and EF's table-rebuild path does not depend on it anyway. The
old hand-written store's `DropColumnIfPresent` helper (`SqliteWorkItemStore.cs:900`) exists
because of that older constraint and would have nothing to do under EF.

**On schemas and sequences:** unsupported, and irrelevant here — this store has no schemas
and no sequences. `EnsureSchema`/`DropSchema` are documented no-ops on SQLite.

**A rebuild is only possible for artifacts that are part of the model.** Anything created by
hand inside a migration raises `NotSupportedException` instead. This store has none.

### The two real constraints

**1. Idempotent scripts are unsupported.** Verified:

```
$ dotnet ef migrations script --idempotent
System.NotSupportedException: Generating idempotent scripts for migrations is not
currently supported for SQLite.
   at …SqliteHistoryRepository.GetEndIfScript()
```

SQLite has no procedural language, so EF cannot generate the `IF` guards an idempotent
script needs ([same page](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)).
For a single-process app with a file database this is mostly harmless — there is no fleet to
run a deployment script against — but it does mean **`dotnet ef database update` is the
supported deployment mechanism, not a checked-in `.sql`**. Worth stating plainly in the PR
so nobody builds a release process around `--idempotent` later.

**2. Transaction semantics, and they changed recently.** EF Core 9 wrapped *all* pending
migrations in one transaction; **EF Core 10 reverted that**, so each migration gets its own
transaction ([Managing Migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing),
citing [dotnet/efcore#35096](https://github.com/dotnet/efcore/issues/35096)). For a
single-developer baseline this changes nothing observable. It is worth knowing that a
*future* multi-migration deploy is not atomic across the batch.

`SaveChanges` itself is transactional as standard — all changes in one call commit or none
([Transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions)). That
preserves the store's two multi-statement writes (`RecordRound` and `RecordDecision` both
write to two tables and rely on atomicity).

### Concurrency: this is the good news

**Verified PRAGMA defaults, and they are the important part of this whole section:**

| | Value | Meaning |
| --- | --- | --- |
| `journal_mode` | **`wal`** | one writer, many concurrent readers |
| `foreign_keys` | `1` | FK constraints **are** enforced |
| `busy_timeout` | `0` | no retry on lock contention |
| `synchronous` | `2` | `FULL` |
| `SQLite` version | `3.53.3` | |

**EF Core turns WAL on; plain ADO.NET does not.** Verified side by side on two databases
created the same minute:

```
EF-created db:    PRAGMA journal_mode = wal
ADO-created db:   PRAGMA journal_mode = delete
```

That is a genuine **improvement** over the current store, which opens raw
`SqliteConnection`s and so runs in rollback-journal mode. Under the current code a board
read that overlaps a loop write can block behind the writer; under EF's WAL it cannot.

**And the actual contention test passed.** 200 `SaveChanges` writes on one `DbContext`
concurrently with 200 reads on another, against the same file:

```
--- concurrent read (board) while writing (loop) ---
  200 writes + 200 reads, no exception.
```

**The one thing to keep an eye on:** `busy_timeout` is `0`. WAL removes writer/reader
contention, but it does **not** remove writer/writer contention — two concurrent *writers*
still serialise on the write lock and, with no busy timeout, can surface `SQLITE_BUSY`
immediately. `SqliteConnectionStringBuilder.DefaultTimeout` defaults to **30** (verified) and
maps to the command timeout, which bounds a single command but is not a lock-retry policy.
The factory is one process with one loop writing serially behind a `SemaphoreSlim`, so this
should not arise — but it is the one place where "Board polls while the loop writes" could
become "Board polls while the loop writes *twice*", and the board is a separate request
thread.

### Foreign keys: a real behaviour change

`foreign_keys=1` by default, and the FK EF generates **is** enforced — verified by inserting
a `round_results` row with no matching work item:

```
Microsoft.Data.Sqlite.SqliteException: SQLite Error 19: 'FOREIGN KEY constraint failed'
```

**The existing hand-written schema declares no foreign keys at all** — `work_items`,
`round_results`, `decisions` and `pending_rounds` are related only by convention and by the
code that writes them. Moving to EF means orphans become *impossible* rather than merely
*absent*. That is an improvement, and it is also a behaviour change: a path that today
writes an orphan would start failing. `PolicyTests` already pins several properties of this
store, so this is the kind of thing worth asserting rather than discovering.

---

## 3. What a `DbContext` costs this specific store

The four tables map to four entity types with no awkwardness. Verified: a baseline migration
was generated against a 1:1 mirror of the schema and the emitted DDL is quoted below.

### The unique index intake depends on — preserved exactly

```csharp
e.HasIndex(x => new { x.RepoUrl, x.IssueNumber })
    .IsUnique()
    .HasDatabaseName("ix_work_items_source_issue");
```

generates

```csharp
migrationBuilder.CreateIndex(
    name: "ix_work_items_source_issue",
    table: "work_items",
    columns: new[] { "repo_url", "issue_number" },
    unique: true);
```

Intake's idempotency keeps working unchanged, and it keeps working for the same reason it
does today: the decision happens *inside* the write, so nothing can slip a second row in
between a check and an insert. The existing `Intake` catches `SqliteException` with
`SqliteErrorCode == 19`; EF surfaces the identical exception for the identical constraint, so
that catch does not change.

### The five diff columns — a table, not a value object

`diff`, `diff_base`, `diff_tree`, `diff_container_bounded`, `diff_unavailable` should stay
**five plain columns on `round_results`**, exactly as they are today. This is the one place
the codebase has already written down the reasoning (`SqliteWorkItemStore.cs:385-393`, and
the "The diff in Review" section of `AGENTS.md`): five plain columns rather than one column
of JSON, because the store is the store of record and a row it cannot read back is an
agent's work nobody can look at.

EF makes that *easier*, not harder — a `DbSet<RoundResultRecord>` with five scalar properties
produces five columns with no configuration at all. The temptation to model `HostDiff` as an
**owned entity** (`OwnsOne`) or a **complex property** should be refused: both would want to
own a shadow key or a discriminator, and either would replace five independently-nullable
columns with a shape that must be deserialised as a unit — reintroducing by the back door the
exact "parses but is missing the field the reviewer needs" failure the current design names
and avoids. `HostDiff` stays a plain CLR record assembled in the repository, from five
scalars.

### Composite keys map cleanly

`round_results` is keyed `(work_item_id, round_number)` and `decisions` is keyed
`(work_item_id, sequence)`. Both are `HasKey(x => new { … })` and both generate the
composite `PRIMARY KEY` the hand-written schema already has.

### Two places EF's model *differs* from today's schema

**(a) Enums: EF stores `INTEGER`; the store stores `TEXT`.** From the generated migration:

```csharp
swimlane = table.Column<int>(type: "INTEGER", nullable: false),
outcome  = table.Column<int>(type: "INTEGER", nullable: false),
decision = table.Column<int>(type: "INTEGER", nullable: false),
applied_to = table.Column<int>(type: "INTEGER", nullable: true),
failure  = table.Column<int>(type: "INTEGER", nullable: true),
kind     = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
```

Verified at runtime: `SELECT DISTINCT swimlane, typeof(swimlane)` → `swimlane=0 typeof=integer`.

Today the store writes `kind.ToString()` / `swimlane.ToString()` and reads back with
`Enum.Parse`, with a deliberate fallback: an unrecognised `kind` reads as `Unrouted` rather
than throwing (`SqliteWorkItemStore.ReadKind`). **EF's `int` storage gives that up.** Two
concrete consequences:

- Reordering or removing an enum member silently reinterprets every existing row. `Swimlane`,
  `WorkItemKind`, `RoundOutcome`, `Decision`, `FailureClass` would all become ordinal-coupled
  to their database representation.
- `defaultValue: 0` is written as a **number**, not a name. It happens to be correct today
  (`WorkItemKind.Unrouted == 0`) and would be wrong the day someone inserts a member above
  it.

Keeping the current text storage is easy — `HasConversion<string>()`. The recommendation is
to keep it, precisely because `Swimlanes` and `Decisions` are **closed vocabularies this
codebase treats as policy**, and a human-readable column is what keeps an operator able to
read a row with the `sqlite3` CLI.

**(b) Foreign keys are added.** Configuring `round_results` with a relationship makes EF emit:

```csharp
table.ForeignKey(
    name: "FK_round_results_work_items_work_item_id",
    principalTable: "work_items",
    onDelete: ReferentialAction.Cascade);
```

Verified present in the migrated database: `FK on round_results: work_items.work_item_id ->
id onDelete=CASCADE`. **Decisions and pending rounds got no FK** unless explicitly
configured. Decide deliberately whether `decisions` should cascade or restrict — the
existing store deletes nothing at all, so cascade is a *new* capability and arguably should
not be granted by default.

### The immutability question — resolved, and it is fine

`WorkItem`, `RoundResultRecord` and `DecisionRecord` are **positional records**, i.e.
immutable. EF normally wants to mutate tracked entities. Verified:

```
INSERT of a positional record: OK
read back: name=orig lane=Backlog
has a parameterless ctor? False
Attempt 1 - mutate a property:  OK, saved.
Attempt 2 - Entry(e).Property("Name").CurrentValue:  OK, 1 row(s) written.
Attempt 3 - ExecuteUpdateAsync:  OK, 1 row(s) written. value now = changed3
```

EF binds a parameterless-less positional record through its primary constructor with no
trouble, and `ExecuteUpdate`/`ExecuteUpdateAsync` covers every targeted write this store
performs — `Move`, `RecordRoute`, `RecordMergeFailure`, `ApplyDecision`, and the
`round_count` bump. The clean shape is therefore: **keep the records as the domain
vocabulary, use `ExecuteUpdate` for the single-column writes, and never expose an EF-tracked
entity above the repository.** That also keeps `IWorkItemStore` as a port, which is the
whole point of the Clean Architecture work this research sits under.

Note `List()` is called from **eight** places and every one filters in memory
(`_store.List().Where(...)`, `.FirstOrDefault(NeedsAnotherMergeAttempt)`,
`.FirstOrDefault(WaitedLongEnough)`, `.FirstOrDefault(item => item.Id == workItemId)`).
EF would translate most of those into SQL `WHERE` clauses, which is strictly better — **but
two of them are the blockers in §4.**

---

## 4. Column type mapping on SQLite, and round-trip fidelity

### What each CLR type becomes

| CLR type | SQLite storage | Verified |
| --- | --- | --- |
| `Guid` | `TEXT` | ✅ matches today |
| `string` | `TEXT` | ✅ |
| `int` | `INTEGER` | ✅ |
| `bool` | `INTEGER` 0/1, `defaultValue: false` | ✅ matches today |
| enum | `INTEGER` | ⚠️ today is `TEXT` — see §3(a) |
| `DateTimeOffset` | `TEXT` | ✅ storage matches; query translation does not |
| `T?` | same, `NULL` | ✅ |

### `DateTimeOffset`: round-trip is perfect

Verified across five cases including non-UTC offsets, sub-second precision and
`DateTimeOffset.MinValue`:

```
utc-truncated   raw='2026-03-01 12:00:00+00:00'                 exact=True  offsetPreserved=True
utc-subsecond   raw='2026-03-01 12:00:00.1234567+00:00'          exact=True  offsetPreserved=True
plus-offset     raw='2026-03-01 12:00:00+05:30'                 exact=True  offsetPreserved=True
minus-offset    raw='2026-03-01 12:00:00-08:00'                 exact=True  offsetPreserved=True
min-year        raw='0001-01-01 00:00:00+00:00'                 exact=True  offsetPreserved=True
```

Every value came back **bit-exact**, offsets preserved. Format is
`yyyy-MM-dd HH:mm:ss.FFFFFFFzzz` — note the **space** separator, not ISO's `T`, and trailing
zeros trimmed (today's store writes `ToString("O")`, which uses `T`). Since a fresh database
is accepted, no data is stranded by the difference, but the *format* is worth stating because
it is what an operator sees in `sqlite3`.

### `DateTimeOffset` ordering: the stored text *is* correctly ordered — EF just refuses to use it

This is the finding that matters most, so it is worth being precise about the distinction.

**(a) The storage format orders correctly.** Raw SQLite `ORDER BY ts` over a deliberately
awkward mix — exact seconds next to 7-digit fractions, straddling a second boundary:

```
ORDER BY ts (lexical, as SQLite does it):
  2026-01-01 09:00:00+00:00
  2026-01-01 10:00:00.0000001+00:00
  2026-01-01 10:00:00.5000000+00:00
  2026-01-01 10:00:00.9999999+00:00
  2026-01-01 10:00:01+00:00
```

which matches chronological order exactly. It works because `'+'` (0x2B) sorts before
`'.'` (0x2E), so a whole second sorts before any fraction of itself.

*(The mixed-offset case does not order correctly — `2026-01-01 05:00:00+00:00` sorts before
`2026-01-01 10:00:00+05:00` for the *same instant*. This repo is safe: `IClock.UtcNow` is
`DateTimeOffset.UtcNow`, so every timestamp written carries offset `+00:00`. That is an
invariant worth a comment, not a coincidence.)*

**(b) EF will not let you ask.** Verified twice:

```
OrderBy THREW: NotSupportedException: SQLite does not support expressions of type
'DateTimeOffset' in ORDER BY clauses. Convert the values to a supported type, or use
LINQ to Objects to order the results on the client side.

comparison THREW: InvalidOperationException: The LINQ expression
'DbSet<WorkItemEntity>().Count(w => w.CreatedUtc >= …)' could not be translated.
```

This is documented behaviour, not a bug: SQLite has no native type for `DateTimeOffset`, so
"other operations, like **comparison and ordering**, will require evaluation on the client"
([SQLite provider limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)).
Equality is documented as supported; `>=` and `ORDER BY` are not, and both throw above.

### What that costs *this* store

Two live call sites, both load-bearing:

1. **`SqliteWorkItemStore.List()`** issues `ORDER BY created_utc, issue_number` — the
   BoardView's query on every poll. As `OrderBy(x => x.CreatedUtc)` this throws.
2. **`Orchestrator.WaitedLongEnough`** (`Orchestrator.cs:754`) and
   **`NeedsAnotherMergeAttempt`** (`Orchestrator.cs:618`) both compare against
   `_clock.UtcNow` — the 48-hour feedback threshold and the merge-retry backoff.

### The fix, and its cost

A value converter to a sortable scalar. Verified end to end:

```csharp
e.Property(x => x.At)
    .HasConversion<long>(
        v => v.UtcTicks,
        v => new DateTimeOffset(v, TimeSpan.Zero));
```

```
OrderBy works: [first, second]
comparison works: Count(At >= t0+12h) = 1
SQL: SELECT "i"."Id", "i"."At", "i"."Counter", "i"."Name" FROM "items" AS "i" ORDER BY "i"."At"
```

Both operations work, and the comparison is pushed **server-side** — the 48-hour threshold
stops being an in-memory scan. The cost is that `created_utc` becomes an `INTEGER` tick
count, which is no longer readable in `sqlite3` and no longer comparable by eye. For a store
whose stated purpose is that a row can be read back and understood, that is a
real trade — and the alternative, sorting in memory with `AsEnumerable()`, gives up
server-side filtering for a board that polls every five seconds.

**Recommendation:** take the converter, and take it **only for the columns that are actually
ordered or compared** (`created_utc`, `review_started_utc`, `merge_retry_after_utc`,
`asked_utc`). Leave the rest — `updated_utc`, `started_utc`, `completed_utc`, `decided_utc`
— as readable text. The columns that need to be *read* and the columns that need to be
*sorted* are not the same set, and nothing forces them to be.

**Not verified:** I did not test whether a `long` converter survives EF's own migration
diffing across a future change of the underlying CLR type, nor how `UtcTicks` behaves for
`DateTimeOffset.MinValue` (it underflows `long.MinValue`). Both should be pinned by a test
before this lands.

---

## 5. The fresh-database path

**`dotnet ef migrations add Baseline` then `dotnet ef database update` is the right
sequence. `EnsureCreated` is the wrong tool here, and it is wrong in a way that is easy to
discover late.**

Verified full run against an empty path:

```
$ dotnet ef database update --context FactoryContext --connection "Data Source=…"
Acquiring an exclusive lock for migration application.
Applying migration '20261015183802_Baseline'.
Done.
```

Resulting objects:

```
table  __EFMigrationsHistory
table  __EFMigrationsLock
table  decisions
table  pending_rounds
table  round_results
table  work_items
index  ix_decisions_work_item
index  ix_work_items_source_issue
```

and the history row:

```
migration applied: 20261015183802_Baseline   product=10.0.13
```

### Why not `EnsureCreated`

Verified: on a database built by `EnsureCreated`,

```
EnsureCreated tables: [items]
has __EFMigrationsHistory? False
Database.EnsureCreated() again returns: False  (false = already there)
```

**`EnsureCreated` creates the schema and no migration history.** The database then exists in
a state EF has no record of, and a later `dotnet ef database update` has nothing to apply
and no baseline to diff against. That is a schema no migration can ever evolve. For a
throwaway test fixture that is fine — and it is *the right answer for the test suite*, which
`AGENTS.md` says builds a temporary SQLite file per test. It is the wrong answer for the
file the factory actually runs on.

**So: `EnsureCreated` in the test host, migrations in the process.** Those are two different
lives for the schema, which is worth saying out loud in the PR so it reads as a decision
rather than an inconsistency.

### `dotnet ef database update --add` is not available

The one-step create-and-apply flag was **added in EF Core 11**, not 10
([Managing Migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing)):
*"Note — This feature was added in EF Core 11."* On 10.0.13 the two commands are separate.

### Two operational notes

- **The `__EFMigrationsLock` table is new machinery.** EF 9 introduced a migration lock; on
  SQLite it is a table plus an inserted row, because SQLite has no session-level advisory
  lock. If the process is killed mid-migration the row can be left behind and *"each attempt
  will wait indefinitely for the lock to be released."* The documented remedy is
  `DROP TABLE "__EFMigrationsLock";`. This is a single-process dev-time file database, so the
  blast radius is one developer's file, but it belongs in a runbook rather than being
  discovered at 9am.
- **Migration files must be committed.** The ticket's premise is that the existing
  `data/agent-factory.db` is not migrated. That is now easy *because* the baseline is a
  committed artefact: the first `Baseline` migration defines the schema for every future one.
  Deleting `data/agent-factory.db` and restarting is the entire cutover — worth writing into
  the PR, because "not migrated" is otherwise ambiguous about what *is* required of whoever
  runs it.

---

## 6. Read performance (Board polls every 5 seconds)

### Connection pooling: already on

Verified against `SqliteConnectionStringBuilder` on Microsoft.Data.Sqlite 10.0.13:

```
default connection string : Data Source=pooltest.db
Pooling default           : true
ForeignKeys default       : null (no pragma sent)
DefaultTimeout default    : 30
Mode default              : ReadWriteCreate
```

Pooling is **enabled by default**, and
[`SqliteConnectionStringBuilder.Pooling`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlite.sqliteconnectionstringbuilder?view=msdata-sqlite-10.0)
(*"Gets or sets a value indicating whether the connection will be pooled"*) is available if
it ever needs turning off. So there is nothing to configure here, and — worth noting given
the current store news up a `SqliteConnection` per call — the change is a **net improvement
over the hand-written store**, which opens and closes a connection on every single operation.

### `AsNoTracking`: matters, but changes no SQL

Verified — the generated SQL is **byte-identical** with and without:

```
TRACKED   : SELECT "w"."id", "w"."base_branch", "w"."created_utc", … FROM "work_items" AS "w"
NOTRACKING: SELECT "w"."id", "w"."base_branch", "w"."created_utc", … FROM "work_items" AS "w"
identical SQL: True
```

So its benefit is purely client-side: skipping change-tracker identity resolution and entity
materialisation. **It should be used on every board read** — a board poll materialises every
work item and then throws it away, and tracking them for a request-scoped context is pure
overhead. But nobody should expect a query-plan win from it, and a performance note claiming
one would be wrong.

### What EF actually changes about the board's query

The board's query is `store.List()` → `SELECT … FROM work_items ORDER BY created_utc,
issue_number`. Under EF that becomes a parameterised query rather than a string-built one
(with `created_utc` reordered to a tick count per §4). Two real gains and one honest
non-gain:

- **Gain:** pooled connections instead of open-per-call, and WAL instead of rollback journal.
- **Gain:** the eight in-memory `List().Where(...)`/`FirstOrDefault(...)` sites become
  server-side `WHERE` clauses, so the loop stops pulling every work item to filter one.
- **Non-gain:** no caching. The board re-reads every 5 seconds, as it does today. EF is not
  a cache and should not be mistaken for one.

**Not verified:** I did not measure throughput on this repository's real data volume. The
concurrency test (200 writes / 200 reads) shows the locking story is sound; it says nothing
about whether a board render of N work items with full round and decision histories stays
inside its 5-second budget. That deserves a measurement once the schema is real.

---

## Summary of recommendations

1. **Packages:** `Microsoft.EntityFrameworkCore.Sqlite` 10.0.13 + `…Design` 10.0.13
   (`PrivateAssets=all`) as `PackageVersion` entries in `Directory.Packages.props`; bump
   `Microsoft.Data.Sqlite` to **10.0.13** in the same change to close the 10.0.12/10.0.13
   split. Note the `SQLitePCLRaw` 2.1.12 → 2.1.13 native-engine bump in the PR.
2. **Tool:** local manifest, committed, **at the path the SDK actually writes** (project root
   on SDK 10.0.401, not `.config/`). `dotnet-ef` 10.0.13, bumped in lockstep.
3. **Schema:** four entities, composite keys as-is, unique index as-is, **five diff columns
   stay five columns** — no `OwnsOne`, no JSON. Enums stay `TEXT` via `HasConversion<string>()`.
4. **Types:** `long` tick converters on `created_utc`, `review_started_utc`,
   `merge_retry_after_utc`, `asked_utc` only. Everything else stays readable text.
5. **Fresh database:** committed `Baseline` migration + `dotnet ef database update`.
   `EnsureCreated` in the test host, migrations in the process — two different lives,
   deliberately. Delete `data/agent-factory.db`; nothing else is required of the operator.
6. **Policy:** `ExecuteUpdate` for every single-column write, so the positional records stay
   immutable and `IWorkItemStore` stays a port with no EF entity above it.
7. **Decide explicitly:** whether `decisions` and `pending_rounds` get foreign keys, and
   whether cascade delete is granted at all. The store has no FKs today; EF adding them by
   default is a change, not a freebie.

## Open questions this research could not settle

- **`DateTimeOffset` equality.** The docs say equality *is* supported; only `>=` and
  `ORDER BY` were tested and both throw. Equality should be verified before relying on it.
- **`DateTimeOffset.MinValue` under `UtcTicks`.** Underflows `long.MinValue`; untested.
- **Converter + future migration diffing.** Whether EF diffs cleanly across a change of the
  converter's CLR type; untested.
- **Board render latency** at real data volumes with full round and decision histories.
- **`__EFMigrationsLock` recovery** under an actual mid-migration kill, rather than from the
  documentation.
- **Whether the `SQLitePCLRaw` 2.1.13** engine changes anything this store depends on
  (`PRAGMA` defaults were read on it, but the suite has never run on it).