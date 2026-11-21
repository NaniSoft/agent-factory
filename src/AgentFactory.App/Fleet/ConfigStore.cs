using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentFactory.App.Fleet;

/// <summary>
/// The fleet's runtime-mutable configuration (ticket 015): one
/// <c>data/config.json</c> document with atomic replace writes, the same
/// pattern as <see cref="RunStore"/>. Ticket 016 laid the store with
/// pipelines and the default assignment; ticket 017 joins fleet targets to
/// it (one store, per the map). Readers get deep copies — a run snapshots its
/// pipeline at start and is immune to edits while in flight.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Flavours persist as names ("opencode-go"), not numbers — the store
        // is hand-editable (ticket 030).
        Converters = { new JsonStringEnumConverter() },
    };

    public const string DefaultPipelineId = "standard";

    private readonly Lock _lock = new();
    private readonly string _storePath;
    // Assigned by LoadFromDisk on every constructor path; null! only appeases
    // definite-assignment analysis across the method call.
    private Document _doc = null!;

    private sealed record Document
    {
        public string DefaultPipelineId { get; set; } = ConfigStore.DefaultPipelineId;
        public List<PipelineDefinition> Pipelines { get; set; } = [];
        public List<TargetRecord> Targets { get; set; } = [];
        public List<OrgRecord> Orgs { get; set; } = [];
        public List<AgentProfile> Profiles { get; set; } = [];
        public UtilityBinding? Utility { get; set; }

        /// <summary>The fleet fallback binding (ticket 044): the profile+model
        /// unpinned phases run on and the shore a dead pinned model lands on.
        /// Same atomic shape as a pin; null = unset, byte-for-byte the
        /// pre-fallback behavior.</summary>
        public UtilityBinding? Fallback { get; set; }
        public bool Paused { get; set; }

        /// <summary>GitHub write token pasted in the web UI (hot path) —
        /// stored literal or `{env:VAR}`; effective token wins over the
        /// bootstrap Secret env and is synced into process env on save so the
        /// next `gh` spawn inherits it with no pod restart. Null = Secret/env
        /// fallback only.</summary>
        public string? GithubToken { get; set; }
    }

    /// <summary>Bootstrap Secret value for GH_TOKEN captured before any
    /// UI overwrite — so clearing the stored token restores the env fallback
    /// instead of losing it.</summary>
    private readonly string? _secretFallbackToken;

    public ConfigStore(string dataDir, string seedRepo)
    {
        _secretFallbackToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        Directory.CreateDirectory(dataDir);
        _storePath = Path.Combine(dataDir, "config.json");
        LoadFromDisk(seedRepo);
        SyncGithubEnvLocked();
    }

    /// <summary>(Re)read the store. Called at construction and whenever the
    /// file's mtime moves — hand edits (the pre-022 surface) and the UI's
    /// writes go live without a restart. A torn or invalid document keeps the
    /// last good one; the next read retries.</summary>
    private void LoadFromDisk(string? seedRepo)
    {
        if (!File.Exists(_storePath))
        {
            // Only the constructor reaches here, with a real seed repo.
            _doc = Seed(seedRepo!);
            _loadedWriteTime = DateTime.MinValue;
            PersistLocked();
            return;
        }

        Document? parsed = null;
        try
        {
            var (json, stripped) = StripUnknownFlavourProfiles(File.ReadAllText(_storePath));
            parsed = JsonSerializer.Deserialize<Document>(json);
            if (stripped) _strippedUnknownFlavours = true;
        }
        catch (JsonException)
        {
            // torn/invalid edit on disk — fall through to the null handling
        }

        if (parsed is null)
        {
            if (seedRepo is { Length: > 0 })
            {
                // Constructor with an unreadable store: run on the seed in
                // memory without persisting — the operator's file is left
                // untouched for repair; the next read picks the fix up.
                _doc = Seed(seedRepo);
                _loadedWriteTime = DateTime.MinValue;
            }
            return; // reload: keep the last good document, retry next read
        }

        _doc = parsed;
        _loadedWriteTime = File.GetLastWriteTimeUtc(_storePath);

        // A config carrying a profile of a flavour this build doesn't know
        // migrates on load: the profile drops, its pins and the utility
        // binding clear — persisted so the file is clean and the migration is
        // one-time.
        var strippedUnknownFlavours = _strippedUnknownFlavours;
        _strippedUnknownFlavours = false;
        if (strippedUnknownFlavours)
            PersistLocked();
    }

    /// <summary>Set by <see cref="StripUnknownFlavourProfiles"/> when the
    /// last read cleaned unknown-flavour profiles out of the document — the
    /// persist happens once the parsed document is in place.</summary>
    private bool _strippedUnknownFlavours;

    /// <summary>Pre-deserialize sanitize: a profile whose Flavour name is not
    /// a defined <see cref="AgentFlavour"/> would otherwise reject the WHOLE
    /// document at enum binding (JsonStringEnumConverter throws), so profiles
    /// of unknown flavours drop here — along with any utility binding or
    /// phase pin that names them (the pin is atomic, profile+model together).
    /// A non-name Flavour token (numeric, from a pre-030 store) counts as
    /// unknown too — its meaning shifted under the enum, so keeping it would
    /// silently rebrand the profile.</summary>
    private static (string Json, bool Migrated) StripUnknownFlavourProfiles(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return (json, false); // torn doc — the deserialize path reports it
        }
        if (root?["Profiles"] is not JsonArray profiles) return (json, false);

        var removed = new HashSet<string>();
        foreach (var node in profiles)
        {
            if (node?["Flavour"] is not { } flavour) continue;
            if (flavour is JsonValue v && v.TryGetValue<string>(out var name)
                && Enum.TryParse<AgentFlavour>(name, ignoreCase: true, out _))
                continue;
            removed.Add(node["Id"]?.GetValue<string>() ?? "");
        }
        if (removed.Count == 0) return (json, false);

        for (var i = profiles.Count - 1; i >= 0; i--)
            if (profiles[i] is { } p && removed.Contains(p["Id"]?.GetValue<string>() ?? ""))
                profiles.RemoveAt(i);

        if (root["Utility"]?["ProfileId"] is { } uid && removed.Contains(uid.GetValue<string>()))
            root["Utility"] = null;

        if (root["Fallback"]?["ProfileId"] is { } fid && removed.Contains(fid.GetValue<string>()))
            root["Fallback"] = null;

        foreach (var phase in root["Pipelines"]?.AsArray()
                     .SelectMany(pl => pl?["Phases"]?.AsArray() ?? [])
                     .Select(ph => ph) ?? [])
        {
            if (phase?["ProfileId"] is { } pid && removed.Contains(pid.GetValue<string>()))
            {
                phase["ProfileId"] = null;
                phase["ModelId"] = null;
            }
        }
        return (root.ToJsonString(), true);
    }

    /// <summary>Reload when the file changed under us. Our own writes update
    /// the tracked stamp, so this fires only on external edits.</summary>
    private void ReloadIfChangedLocked()
    {
        if (File.Exists(_storePath)
            && File.GetLastWriteTimeUtc(_storePath) != _loadedWriteTime)
        {
            LoadFromDisk(null!); // file exists, so the seed never applies
        }
    }

    private DateTime _loadedWriteTime;

    /// <summary>The effective GitHub token: stored UI token (resolved
    /// `{env:}`) when set, else the bootstrap Secret/env value. This is
    /// what `gh` spawns see via process env (synced by SyncGithubEnvLocked).</summary>
    public string? EffectiveGithubToken()
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            var stored = ProfileCredential.Resolve(_doc.GithubToken);
            return string.IsNullOrWhiteSpace(stored) ? _secretFallbackToken : stored;
        }
    }

    /// <summary>Whether a stored (UI) token exists — for masking in the API.</summary>
    public bool HasStoredGithubToken()
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            return !string.IsNullOrWhiteSpace(_doc.GithubToken);
        }
    }

    /// <summary>Save the UI token (null/blank clears to Secret fallback) and
    /// sync process env so the next `gh` spawn inherits it immediately —
    /// no pod restart, no 60s poll wait for the key itself.</summary>
    public void SetGithubToken(string? token)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            _doc.GithubToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            PersistLocked();
            SyncGithubEnvLocked();
        }
    }

    private void SyncGithubEnvLocked()
    {
        var stored = ProfileCredential.Resolve(_doc.GithubToken);
        var effective = string.IsNullOrWhiteSpace(stored) ? _secretFallbackToken : stored;
        Environment.SetEnvironmentVariable("GH_TOKEN", effective);
    }

    /// <summary>Deep copy of the fleet's default pipeline — what a run walks
    /// when its target has no explicit assignment.</summary>
    public PipelineDefinition DefaultPipeline()
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            return (_doc.Pipelines.FirstOrDefault(p => p.Id == _doc.DefaultPipelineId)
                    ?? _doc.Pipelines.FirstOrDefault()
                    ?? Seed("").Pipelines[0]).Clone();
        }
    }

    /// <summary>Deep copy of the target a run executes against, or null when
    /// the repo is no longer enrolled (the run fails fast at start).</summary>
    public TargetRecord? TargetFor(string repo)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            return _doc.Targets.FirstOrDefault(t => t.Repo == repo)?.Clone();
        }
    }

    /// <summary>Deep copy of every enrolled target — the poller's tour.</summary>
    public IReadOnlyList<TargetRecord> Targets()
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            return [.. _doc.Targets.Select(t => t.Clone())];
        }
    }

    /// <summary>Deep copy of every enrolled org — the scan loop's tour
    /// (ticket 018).</summary>
    public IReadOnlyList<OrgRecord> Orgs()
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            return [.. _doc.Orgs.Select(o => new OrgRecord
            {
                Org = o.Org,
                Intake = o.Intake,
                PipelineId = o.PipelineId,
                Enabled = o.Enabled,
                DefinitionOfDone = o.DefinitionOfDone,
            })];
        }
    }

    /// <summary>Fleet pause (ticket 018): true stops dequeueing — scanning and
    /// enqueueing continue, in-flight runs complete.</summary>
    public bool IsPaused()
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            return _doc.Paused;
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            _doc.Paused = paused;
            PersistLocked();
        }
    }

    /// <summary>Deep copy of every pipeline — the Pipelines page's working set
    /// (ticket 021).</summary>
    public IReadOnlyList<PipelineDefinition> Pipelines()
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            return [.. _doc.Pipelines.Select(p => p.Clone())];
        }
    }

    /// <summary>The fleet default's id (the pipeline targets without an
    /// explicit assignment walk).</summary>
    public string CurrentDefaultPipelineId()
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            return _doc.DefaultPipelineId;
        }
    }

    /// <summary>Add or replace a pipeline by id (the Pipelines page's save,
    /// ticket 021). In-flight runs are immune — they hold snapshots.</summary>
    public void SavePipeline(PipelineDefinition pipeline)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            var existing = _doc.Pipelines.FirstOrDefault(p => p.Id == pipeline.Id);
            if (existing is null) _doc.Pipelines.Add(pipeline.Clone());
            else _doc.Pipelines[_doc.Pipelines.IndexOf(existing)] = pipeline.Clone();
            PersistLocked();
        }
    }

    /// <summary>Make a pipeline the fleet default (must exist).</summary>
    public void SetDefaultPipeline(string id)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            if (_doc.Pipelines.All(p => p.Id != id))
            {
                throw new InvalidOperationException($"no pipeline with id {id}");
            }
            _doc.DefaultPipelineId = id;
            PersistLocked();
        }
    }

    /// <summary>Delete a pipeline, guarded per ticket 015: the default can't
    /// go until a new default is chosen, and an assigned pipeline can't go
    /// until every target and org referencing it is reassigned.</summary>
    public (bool Ok, string? Error) DeletePipeline(string id)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            if (id == _doc.DefaultPipelineId)
            {
                return (false, $"this pipeline is the fleet default — choose a new default first");
            }
            var assigned = _doc.Targets.Where(t => t.PipelineId == id).Select(t => t.Repo)
                .Concat(_doc.Orgs.Where(o => o.PipelineId == id).Select(o => o.Org + " (org)"))
                .ToList();
            if (assigned.Count > 0)
            {
                return (false, "assigned to: " + string.Join(", ", assigned) + " — reassign before deleting");
            }
            if (_doc.Pipelines.RemoveAll(p => p.Id == id) == 0)
            {
                return (false, "no such pipeline");
            }
            PersistLocked();
            return (true, null);
        }
    }

    // --- Agent profiles + the utility binding (ticket 030) ---

    /// <summary>Deep copy of every profile — the Profiles page's working set
    /// (ticket 031); until then also the hand-edited store's read surface.</summary>
    public IReadOnlyList<AgentProfile> Profiles()
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            return [.. _doc.Profiles.Select(p => p.Clone())];
        }
    }

    /// <summary>Deep copy of one profile by id, or null when gone — a run's
    /// pin resolution treats null as a fail-fast condition, not a fallback.</summary>
    public AgentProfile? ProfileFor(string id)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            return _doc.Profiles.FirstOrDefault(p => p.Id == id)?.Clone();
        }
    }

    /// <summary>Add or replace a profile by id (hand-edited until the 031 UI).
    /// In-flight runs hold their own snapshots and are immune.</summary>
    public void SaveProfile(AgentProfile profile)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            var existing = _doc.Profiles.FirstOrDefault(p => p.Id == profile.Id);
            if (existing is null) _doc.Profiles.Add(profile.Clone());
            else _doc.Profiles[_doc.Profiles.IndexOf(existing)] = profile.Clone();
            PersistLocked();
        }
    }

    /// <summary>Delete a profile, guarded per ticket 028: any pipeline phase
    /// pinning it — or the utility binding — blocks with the referrer list.</summary>
    public (bool Ok, string? Error) DeleteProfile(string id)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            var referrers = _doc.Pipelines
                .SelectMany(pl => pl.Phases.Where(ph => ph.ProfileId == id)
                    .Select(ph => $"{pl.Name} › {ph.Name}"))
                .ToList();
            if (_doc.Utility?.ProfileId == id)
                referrers.Add("utility binding (triage/planning)");
            if (_doc.Fallback?.ProfileId == id)
                referrers.Add("fallback binding (pin fallback / unpinned default)");
            if (referrers.Count > 0)
                return (false, "pinned by: " + string.Join(", ", referrers) + " — unpin before deleting");
            if (_doc.Profiles.RemoveAll(p => p.Id == id) == 0)
                return (false, "no such profile");
            PersistLocked();
            return (true, null);
        }
    }

    /// <summary>Deep copy of the utility binding, or null = triage/planning
    /// sessions run on engine defaults (ticket 028).</summary>
    public UtilityBinding? Utility()
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            return _doc.Utility?.Clone();
        }
    }

    /// <summary>Set or clear the utility binding. The profile must exist and,
    /// when a model is named, it must be in that profile's palette; the flavour
    /// must have an execution adapter.</summary>
    public void SetUtility(UtilityBinding? binding)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            GuardBinding(binding);
            _doc.Utility = binding is null ? null : binding.Clone();
            PersistLocked();
        }
    }

    /// <summary>Deep copy of the fleet fallback binding (ticket 044), or null
    /// = unpinned phases run engine defaults and dead pins have no shore.</summary>
    public UtilityBinding? Fallback()
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            return _doc.Fallback?.Clone();
        }
    }

    /// <summary>Set or clear the fallback binding (ticket 044). Atomic unlike
    /// the utility binding — profile AND model, or unset — because every
    /// consumer needs a concrete model to probe and to run.</summary>
    public void SetFallback(UtilityBinding? binding)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            if (binding?.ProfileId is { Length: > 0 } && binding.ModelId is not { Length: > 0 })
                throw new InvalidOperationException(
                    "the fallback binding is atomic — profile and model together, or unset");
            GuardBinding(binding);
            _doc.Fallback = binding is null ? null : binding.Clone();
            PersistLocked();
        }
    }

    /// <summary>The two fleet bindings share one guard: the profile must
    /// exist, a named model must be in its palette, and the flavour must have
    /// an execution adapter (the store refuses bindings it can't honor,
    /// ticket 028).</summary>
    private void GuardBinding(UtilityBinding? binding)
    {
        if (binding?.ProfileId is not { Length: > 0 } pid) return;
        var profile = _doc.Profiles.FirstOrDefault(p => p.Id == pid)
            ?? throw new InvalidOperationException($"no profile with id {pid}");
        if (binding.ModelId is { Length: > 0 } mid && !profile.Models.Contains(mid))
            throw new InvalidOperationException(
                $"model {mid} is not in profile '{profile.Name}'s palette");
        if (!AgentFlavours.Executable(profile.Flavour))
            throw new InvalidOperationException(
                $"flavour {AgentFlavours.Label(profile.Flavour)} has no execution adapter yet");
    }

    /// <summary>Enroll a discovered repo as a fleet target (org scan or the
    /// 022 UI). A repo already enrolled is left untouched.</summary>
    public void AddTarget(TargetRecord target)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            if (_doc.Targets.Any(t => t.Repo == target.Repo)) return;
            _doc.Targets.Add(target.Clone());
            PersistLocked();
        }
    }

    /// <summary>Un-enroll repos (org-removed, or the 022 UI): their records
    /// drop from the store; queued runs are the caller's reconcile to cancel
    /// (ticket 018); history lives in the run store.</summary>
    public void RemoveTargets(IEnumerable<string> repos)
    {
        var set = repos.ToHashSet();
        lock (_lock)
        {
            ReloadIfChangedLocked();
            if (_doc.Targets.RemoveAll(t => set.Contains(t.Repo)) > 0) PersistLocked();
        }
    }

    /// <summary>Enroll an org (ticket 022): the scan loop materializes its
    /// repos into targets. An already-enrolled org is left untouched.</summary>
    public void AddOrg(OrgRecord org)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            if (_doc.Orgs.Any(o => o.Org == org.Org)) return;
            _doc.Orgs.Add(new OrgRecord
            {
                Org = org.Org,
                Intake = org.Intake,
                PipelineId = org.PipelineId,
                Enabled = org.Enabled,
                DefinitionOfDone = org.DefinitionOfDone,
            });
            PersistLocked();
        }
    }

    /// <summary>Remove an org enrollment: the scanner's stale-snapshot sweep
    /// un-enrolls its targets on the next scan (queued runs are cancelled by
    /// the runner's reconcile).</summary>
    public void RemoveOrg(string org)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            if (_doc.Orgs.RemoveAll(o => o.Org == org) > 0) PersistLocked();
        }
    }

    /// <summary>Replace a target's settings (the Targets drawer save, ticket
    /// 022). Future runs pick the change up; in-flight runs hold snapshots.</summary>
    public void UpdateTarget(TargetRecord updated)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            var existing = _doc.Targets.FirstOrDefault(t => t.Repo == updated.Repo)
                ?? throw new InvalidOperationException($"no enrolled target {updated.Repo}");
            _doc.Targets[_doc.Targets.IndexOf(existing)] = updated.Clone();
            PersistLocked();
        }
    }

    /// <summary>Replace an org's settings — they materialize into newly
    /// discovered targets only; already-discovered targets are edited
    /// individually (ticket 013/018).</summary>
    public void UpdateOrg(OrgRecord updated)
    {
        lock (_lock)
        {
            ReloadIfChangedLocked();
            var existing = _doc.Orgs.FirstOrDefault(o => o.Org == updated.Org)
                ?? throw new InvalidOperationException($"no enrolled org {updated.Org}");
            _doc.Orgs[_doc.Orgs.IndexOf(existing)] = new OrgRecord
            {
                Org = updated.Org,
                Intake = updated.Intake,
                PipelineId = updated.PipelineId,
                Enabled = updated.Enabled,
                DefinitionOfDone = updated.DefinitionOfDone,
            };
            PersistLocked();
        }
    }

    /// <summary>The pipeline a target walks: its explicit assignment when the
    /// id resolves, else the fleet default (most-specific-wins, ticket 013).</summary>
    public PipelineDefinition PipelineFor(TargetRecord target)
    {
        lock (_lock)
        {
            if (target.PipelineId is { } id
                && _doc.Pipelines.FirstOrDefault(p => p.Id == id) is { } assigned)
            {
                return assigned.Clone();
            }
            return DefaultPipeline();
        }
    }

    /// <summary>The playground repo's enrollment (ticket 017 seed): label
    /// intake, fleet-default pipeline, dotnet DoD — v1 behavior preserved.</summary>
    private static TargetRecord SeedTarget(string repo) => new()
    {
        Repo = repo,
        Intake = IntakeRules.Label,
        Enabled = true,
        DefinitionOfDone = "dotnet build && dotnet test",
    };

    private static Document Seed(string seedRepo) => new()
    {
        DefaultPipelineId = DefaultPipelineId,
        Targets = string.IsNullOrWhiteSpace(seedRepo) ? [] : [SeedTarget(seedRepo)],
        Pipelines =
        [
            new PipelineDefinition
            {
                Id = DefaultPipelineId,
                Name = "Standard",
                Phases =
                [
                    new PhaseDefinition
                    {
                        Name = "develop",
                        Skills = ["tdd"],
                        // v1's house prompt (PromptBuilder.Build), placeholderized,
                        // with the phase's skills named as a nudge (ticket 011).
                        Template =
"""
You are an Agent Factory fleet agent implementing a GitHub issue in the repo
https://github.com/{{repo}}.

Your workspace is this git worktree (your working directory), checked out on
branch `{{branch}}` (cut from {{base}}). Work only inside this worktree; do
not touch any other directory, do not switch branches, do not run `git push` —
the host owns the push and PR flow.

---

THE ISSUE, VERBATIM:

# {{issue_title}}

{{issue_body}}

---

DEFINITION OF DONE — before you finish, all of these must hold:

1. The issue is implemented in this worktree.
2. As your final step you run the definition-of-done commands `{{definition_of_done}}`
   in the repo root of the worktree, and they succeed. Fix failures until they pass.
3. Every change is committed on branch {{branch}} with a message that starts with
   `Implement #<issue-number>: ` — the issue's number is the number in your branch
   name (agent/issue-<n>-…). Commit everything you changed — uncommitted work is lost work.

CONSTRAINTS:

- Do not force-push, rebase onto other branches, or rewrite {{base}}.
- Do not create, delete, or push branches; do not open PRs or comment on GitHub.
- `opencode.json` at the repo root is fleet-local permission config: never commit,
  stage, or edit it.
- Keep the code style of the surrounding codebase.

SKILLS: use the `tdd` skill — work test-first where the issue admits it.
""",
                    },
                    new PhaseDefinition
                    {
                        Name = "review",
                        Skills = ["code-review", "pr"],
                        // Critique-and-fix on the same branch; the host opens the
                        // PR (ADR 0001 — the `pr` skill is a playbook, not a license).
                        Template =
"""
You are an Agent Factory fleet review agent. The develop phase has implemented
GitHub issue "{{issue_title}}" in https://github.com/{{repo}} on branch
`{{branch}}` (cut from {{base}}), in this git worktree (your working directory).

THE ISSUE, VERBATIM:

# {{issue_title}}

{{issue_body}}

---

Your job: review the changes on this branch (from the worktree root,
`git diff {{base}}...HEAD`) against a landable-PR bar, and FIX what you find —
edit in place, on this same branch. This is critique-and-fix, not commentary:
do not leave known problems for the human reviewer.

Check at least: correctness against the issue; missing or weak tests; stray
debug or dead code; unintended files in the diff; style drifting from the
surrounding code.

DEFINITION OF DONE — before you finish, all of these must hold:

1. The branch's changes are as correct as you can make them, and every fix is
   committed on branch {{branch}} (same `Implement #<issue-number>: …` style,
   or `Review #<issue-number>: …` for review fixes).
2. As your final step you run the definition-of-done commands `{{definition_of_done}}`
   in the repo root of the worktree, and they succeed. Fix failures until they pass.

CONSTRAINTS:

- Do not force-push, rebase onto other branches, or rewrite {{base}}.
- Do not create, delete, or push branches; do not open PRs or comment on GitHub —
  the host opens the PR.
- `opencode.json` at the repo root is fleet-local permission config: never commit,
  stage, or edit it.
- Keep the code style of the surrounding codebase.

SKILLS: use the `code-review` skill as your review playbook and the `pr`
skill's landable-PR bar for what "done" means. They steer your review only —
you must not run `gh` or open PRs yourself.
""",
                    },
                ],
            },
        ],
    };

    private void PersistLocked()
    {
        var tmp = _storePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_doc, JsonOpts));
        File.Move(tmp, _storePath, overwrite: true);
        _loadedWriteTime = File.GetLastWriteTimeUtc(_storePath);
    }
}
