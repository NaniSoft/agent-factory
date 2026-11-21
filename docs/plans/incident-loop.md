# Plan — the incident loop

**Status:** proposed
**Scope:** Agent Factory + a new signal intake + cluster-side deployment
**Prerequisite:** the cluster can POST an Alertmanager-v4-format webhook. The
contract is deliberately detector-agnostic: Prometheus Alertmanager speaks it
natively, Grafana Alerting and most SaaS alert tools can be normalized into it,
and a cron job can hand-write it.

---

## 1. Goal

Close the loop **telemetry → ticket → run → PR → human approval → deploy →
verified recovery**, with one new seam in the factory: a **signal intake** that
turns a detected incident into a labelled GitHub issue. The existing intake,
run, pipeline and PR machinery is reused unchanged.

The agent never watches the cluster. Rules detect; the agent repairs. That
split is the whole design, and every decision below follows from it.

## 2. Non-goals

- No LLM in the detection path. No continuous log-reading session.
- No cluster mutation from the factory or from an agent session. GitOps applies.
- No auto-merge. The PR review is the human gate.
- No replacement of the observability stack. The factory consumes its output.
- No multi-instance fleet. The intake inherits the single-instance contract
  (`GLOSSARY.md` → Deployment): one fleet, one signal store, one loop.

## 3. The contract (invariants)

1. **One incident, one issue.** A fingerprint identifies the incident; repeats
   update the record, never open a second issue.
2. **Durable before processed.** The webhook writes the raw payload to disk and
   returns. Processing is a retried loop step, not request work.
3. **Evidence travels with the ticket.** By the time a run starts, the pod is
   gone; the issue body must already carry the logs, events and metric window.
4. **Routing is deterministic.** Alert → repo comes from an explicit label or a
   configured map, never from a model's guess.
5. **The agent never has cluster credentials.** Read access lives in the intake,
   on the host, exactly like the GitHub write token.
6. **Every cap is loud.** A suppressed signal is recorded with a reason and is
   visible on the board, not silently dropped.

## 4. Where it sits in the code

```
POST /api/signals/alertmanager
        │  (secret check, write raw payload)
        ▼
data/signals/inbox/*.json
        │  drained once per fleet cycle
        ▼
Fleet/SignalIntake ──► Fleet/SignalStore (data/signals.json)
        │                 fingerprint → occurrence → state
        ├──► Fleet/EvidenceCollector ──► Prometheus / Loki / kubectl (read-only)
        └──► GitHubService.CreateIssueAsync(repo, …, "agent-factory")
                                        │
                                existing FleetRunner label poll
                                        ▼
                        RunRecord (Kind = Incident, Priority, SignalId)
                                        ▼
                    incident pipeline: diagnose → fix → verify
                                        ▼
                              PR → human merge → GitOps rollout
                                        ▼
                     Fleet/IncidentVerifier: query again, close or escalate
```

The cycle in `FleetRunner.ExecuteAsync` grows two steps and keeps its shape:

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    await signals.DrainInboxAsync(stoppingToken);   // new: webhook → issue
    await orgs.ScanAsync(stoppingToken);
    await PollAndEnqueueAsync(stoppingToken);
    CancelUnenrolledQueued();
    await signals.VerifyAsync(stoppingToken);       // new: merged PR → recovered?
    await DrainQueueAsync(stoppingToken);
    await DelayOrWakeAsync(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
}
```

## 5. Work breakdown

Effort: **S** ≈ half a day, **M** ≈ 1–2 days, **L** ≈ 3–5 days.

### M0 — Foundations and the decision record (S)

Nothing observable changes; the shapes and stores land first.

- [ ] `docs/adr/0007-telemetry-is-a-second-intake.md` — detect deterministically,
      repair agentically; the issue is the seam; one incident one issue; the
      agent never gets cluster credentials.
- [ ] `GLOSSARY.md` — add **Signal**, **Signal source**, **Incident**,
      **Evidence**, **Fingerprint**, **Verification window**. Avoid: alert-as-run,
      monitor, watcher.
- [ ] `Fleet/SignalModels.cs` — `SignalRecord`, `SignalState`
      (`Received | Ticketed | Running | PrOpen | Merged | Verified | Suppressed |
      Recurred | DeadLetter`), `SignalClass`
      (`Code | Capacity | Infra | Transient`), `SignalFingerprint`.
- [ ] `Fleet/SignalStore.cs` — `data/signals.json`, atomic replace, same pattern
      as `RunStore`; a fingerprint index; `data/signals/inbox/`, `done/`, `dead/`.
- [ ] `FleetOptions.Sentinel` — `InboxDir`, `WebhookSecretEnv`
      (default `SENTINEL_WEBHOOK_SECRET`), `MaxPayloadBytes`, `EvidenceDir`.
      Bind as `Fleet__Sentinel__*`.
- [ ] `ConfigStore.Document.Signals` — `SignalOptions` (fleet-level gates) and
      `List<SignalSourceRecord>` (per-source routing). Writable later via API.

**Acceptance:** store round-trips; fingerprint is stable across pod-name churn;
`dotnet test` green.

### M1 — Alert in, issue out (the seam) (M)

- [ ] `POST /api/signals/alertmanager` in `Api/AgentFactoryApi.cs`:
      constant-time secret comparison, body-size cap, write payload to the inbox,
      return `202`. Return `401` only on bad auth, `400` only on unparseable
      JSON; otherwise always `2xx` (Alertmanager retries on non-2xx, and we do
      not want it to).
- [ ] `Fleet/SignalIntake.DrainInboxAsync` — oldest file first; normalize the
      Alertmanager v4 payload per alert; compute the fingerprint; apply gates;
      create or update; archive the file. Failures increment an attempt counter
      beside the file and retry next cycle; 5 attempts → `dead/` plus a log line
      and a board-visible `DeadLetter` record.
- [ ] **Fingerprint** = `sha256(sourceId | alertname | namespace | workload)`.
      Workload resolution order: `agent_factory_workload` label →
      `deployment` → `statefulset` → `job` → `app_kubernetes_io_name` → `pod`
      with the replica hash stripped. Never hash Alertmanager's own
      `fingerprint` — it includes volatile labels.
- [ ] **Gates**, each recording its reason: severity floor, `MaxTicketsPerHour`,
      `MaxOpenIncidentRuns`, dedup window (`DedupWindowHours`), class
      `Transient`/`Infra` (no ticket).
- [ ] **Routing**, deterministic order: alert label `agent_factory_repo` →
      source `NamespaceRepoMap[namespace]` → source `TargetRepo`. No match is a
      `Suppressed` record with reason `unrouted`, never a guess.
- [ ] **Issue creation** — existing `GitHubService.CreateIssueAsync` with the
      opt-in label plus `agent-factory:incident`. Ensure both labels once per
      repo (generalize `EnsureOptInLabelExists`). Body carries a hidden marker
      `<!-- af-signal:<fingerprint> -->` for reconciliation after a volume wipe.
- [ ] **Dry-run** (`Signals.DryRun`): render the body to
      `data/incidents/<fp>/issue.md`, log the would-be ticket, create nothing.
      This is the tuning mode for week one.
- [ ] `ISignalIssueSink` — a one-class seam over the three `GitHubService` calls
      the intake needs (`CreateIssueAsync`, `CommentOnIssue`, `CloseIssueAsync`),
      so `SignalIntake` is testable without `gh`.

**Acceptance:** a recorded Alertmanager payload produces exactly one issue; the
same payload twice produces zero new issues; dry-run writes a file and calls no
CLI; a bad secret returns `401`.

### M2 — Evidence collection (M)

A fix is only as good as the ticket. This milestone is where run quality is won.

- [ ] `Fleet/EvidenceCollector.cs` + `IEvidenceSource`:
      - `PrometheusEvidence` — honour an `agent_factory_query` annotation on the
        alert rule; fall back to the expression in `generatorURL`. Query
        `api/v1/query_range` over `[startsAt - 15m, now]`, render a compact
        value table.
      - `LokiEvidence` — `agent_factory_logql` annotation; last N lines, capped.
      - `KubernetesEvidence` — `kubectl -n <ns> get pods -l <selector> -o wide`,
        `describe pod`, `logs --previous --tail=200` per non-ready replica,
        `get events --sort-by=.lastTimestamp`. Read-only.
- [ ] Snapshot to `data/incidents/<fingerprint>/<collectedAt>/` and render a
      bounded `<details>` block into the issue. Hard budget
      `Signals.IssueBodyLimitBytes` (55 000 — GitHub's limit is 65 536);
      truncate per source with a pointer to the PVC path.
- [ ] **Redaction** — never `describe secret`, never environment values; drop
      lines matching a configured regex list; cap each source independently.
- [ ] `PromptBuilder.IncidentIssueBody(...)` — host-authored, deterministic:
      what fired, where, since when, the metric window, the logs, the recent
      deploys/commits in the routed repo, the runbook link, the query to check.

**Acceptance:** a golden-file test renders a full issue body from a recorded
payload; no source can push the body over the limit; redaction strips a planted
secret.

### M3 — Incident runs and the priority lane (M)

- [ ] `RunKind.Incident` appended after `Child` (states persist as numbers —
      never insert mid-sequence; see `RunModels.cs`).
- [ ] `RunRecord` gains `SignalId`, `SignalFingerprint`, `Priority` (0/1/2 from
      severity), and optional `PipelineId` (the source's pipeline override).
- [ ] `ConfigStore.PipelineById(id)`; `RunExecutor` walks
      `run.PipelineId` when set and fails fast when it has gone — the same
      fail-fast posture as an unwrapped pin.
- [ ] `FleetRunner.PickNextRun` — incidents first (priority desc, then oldest),
      then the existing per-repo round-robin. Feature fairness is preserved.
- [ ] Caps enforced at creation time in `SignalIntake`, not at dequeue: a
      queued incident that was never ticketed is invisible to the operator.
- [ ] `PromptBuilder.PrBody` — incident variant that requires **Root cause**,
      **Why this is not a symptom mask**, and **Detection** (what would have
      caught it earlier) sections.

**Acceptance:** priority ordering is a pure, tested function; an incident jumps
ahead of a queued backlog; `PipelineId` resolves and fails fast when dangling.

### M4 — The incident pipeline (M)

- [ ] `ConfigStore` seeds a pipeline `incident` (`diagnose → fix → verify`) and
      sets `Document.IncidentPipelineSeeded`, so the seed is one-time and an
      operator may delete it. Existing volumes get the migration on load.
- [ ] **diagnose** — reproduce from the evidence, localize the faulty path,
      write the failing test first. Explicitly: no production edits, no cluster
      access, work from the evidence in the issue.
- [ ] **fix** — smallest change that makes that test pass. Skills: `tdd`.
- [ ] **verify** — `{{definition_of_done}}`, plus a written statement that the
      new test fails on `{{base}}` and passes on `{{branch}}`.
- [ ] No new placeholders. The evidence rides in `{{issue_body}}` because the
      issue body *is* the evidence — `Placeholders.All` and its run-start
      validation stay untouched.

**Acceptance:** the pipeline validates at run start; a template using an unknown
placeholder still fails fast; the PR body carries the three sections.

### M5 — Closing the loop (L)

- [ ] **Resolve handling** — `send_resolved: true`; a resolved alert comments on
      the issue and closes it when no run started (recorded as a transient, a
      tuning data point) or leaves it alone when a PR is open.
- [ ] `Fleet/IncidentVerifier.cs`, called from the cycle:
      1. For `PrOpen` incidents, poll PR state; on merge stamp `MergedAt` and set
         `VerificationDueAt = mergedAt + Signals.VerificationWindowHours`.
      2. For due incidents, re-run the alert's query over the window. Recovered →
         comment the before/after, close, `Verified`. Not recovered →
         `Recurred`.
- [ ] **Incident memory** — a fingerprint that recurs after `MergedAt` comments
      on the *existing* issue and labels `agent-factory:recurred`; it never opens
      a second ticket. Two recurrences escalates to a human instead of running
      again, because the diagnosis is probably wrong.
- [ ] `GitHubService` additions: `CloseIssueAsync`, `GetIssueStateAsync`,
      `GetPullRequestStateAsync`.

**Acceptance:** verifier decisions are a tested table; a recurrence never
creates a duplicate issue; recovery closes with numbers attached.

### M6 — Board and self-observability (M)

- [ ] API: `GET /api/signals` (filter by state/source/repo),
      `GET /api/signals/{id}`, `GET /api/sources`, `PUT /api/sources`,
      `POST /api/signals/redeliver` (replay a dead-letter from disk).
- [ ] Board page `app/signals/page.tsx` — read-only feed: state, fingerprint,
      occurrence count, suppression reason, issue and PR links. Sources are a
      writable drawer, like the Targets page. Follow `ClientApp/AGENTS.md`:
      render what the API answers, write only through explicit commands.
- [ ] Loop metrics on the factory itself — `signals_received`, `deduped`,
      `suppressed{reason}`, `tickets_opened`, `incident_runs{outcome}`,
      `time_to_ticket`. A hand-written `/metrics` text exposition keeps the
      zero-NuGet posture; add `prometheus-net` only if richer labels are needed.
- [ ] `ServiceMonitor` (or scrape annotations) so the cluster's Prometheus
      scrapes the factory — the loop observes itself.

**Acceptance:** a suppressed signal is visible with its reason; counts match the
store; `/metrics` scrapes clean.

### M7 — Deployment (M)

- [ ] `deploy/k8s/rbac.yaml` — ServiceAccount `agent-factory`; ClusterRole
      `get/list/watch` on pods, pods/log, events, deployments, replicasets,
      statefulsets, daemonsets, nodes, namespaces. Nothing writable.
- [ ] `deployment.yaml` — `serviceAccountName`, plus the `Fleet__Sentinel__*`
      keys in the ConfigMap.
- [ ] Secret — `SENTINEL_WEBHOOK_SECRET` alongside `GO_API_KEY` and `GH_TOKEN`.
- [ ] `deploy/k8s/networkpolicy.yaml` — ingress to `:8080` from the monitoring
      namespace (where Alertmanager runs) and the operator's path only.
- [ ] `Dockerfile` — pin and install `kubectl` for evidence collection, in the
      same style as `gh` and `opencode`. No new .NET package.
- [ ] `deploy/k8s/README.md` — the wiring: Alertmanager receiver, the
      `agent_factory/repo` annotation contract, the dry-run week, the caps.

**Acceptance:** a pod with the ServiceAccount can describe a pod in another
namespace and cannot write; Alertmanager reaches the endpoint through the
ClusterIP service; the image carries `kubectl`.

## 6. Data shapes

```jsonc
// data/config.json → Signals
{
  "Signals": {
    "Enabled": true,
    "DryRun": true,
    "MinSeverity": "warning",
    "MaxTicketsPerHour": 6,
    "MaxOpenIncidentRuns": 3,
    "DedupWindowHours": 24,
    "VerificationWindowHours": 6,
    "IssueBodyLimitBytes": 55000,
    "Sources": [
      {
        "Id": "prom-prod",
        "Kind": "alertmanager",
        "Enabled": true,
        "TargetRepo": "NaniSoft/checkout-service",
        "PipelineId": "incident",
        "NamespaceRepoMap": { "checkout": "NaniSoft/checkout-service" }
      }
    ]
  }
}
```

```jsonc
// data/signals.json → one record
{
  "Id": "sig-20261121-101500-01",
  "Fingerprint": "9f2c…",
  "SourceId": "prom-prod",
  "AlertName": "CheckoutErrorBudgetFastBurn",
  "Namespace": "checkout",
  "Workload": "checkout-service",
  "Severity": "critical",
  "Class": "Code",
  "State": "Ticketed",
  "Occurrences": 3,
  "FirstSeenAt": "2026-11-21T10:02:00Z",
  "LastSeenAt": "2026-11-21T10:15:00Z",
  "IssueUrl": "https://github.com/NaniSoft/checkout-service/issues/42",
  "IssueNumber": 42,
  "RunId": null,
  "PrUrl": null,
  "MergedAt": null,
  "VerifiedAt": null,
  "RecurredAfterPr": false,
  "SuppressedReason": null,
  "EvidenceDir": "/data/incidents/9f2c…/20261121-101500"
}
```

## 7. Classification — what earns a run

| Class | Examples | Fleet action |
|---|---|---|
| **Code** | 5xx burn, p99 regression after a rollout, panic burst | Ticket → incident run |
| **Capacity** | OOMKilled, CPU throttling at limit, HPA pinned | `agent-factory:capacity` ticket, no run by default |
| **Infra** | node pressure, disk, Pending pods, CNI | Notify platform; no ticket in app repos |
| **Transient** | single-sample spike, resolved before dedup window | Suppressed, counted as tuning data |

The class is decided from labels the rule author sets
(`agent_factory_class`, default `code` for severity ≥ warning), never inferred
in prose. OOM is the trap: a model asked to "fix OOM" will raise a limit, which
buys a node instead of fixing a leak.

## 8. Configuration the cluster side needs

Rule template annotations (the whole contract, in one place):

```yaml
annotations:
  summary: "checkout 5xx burn 14.4x"
  runbook_url: "https://runbooks/checkout-5xx"
  agent_factory_repo: "NaniSoft/checkout-service"
  agent_factory_workload: "checkout-service"
  agent_factory_class: "code"
  agent_factory_query: 'sum(rate(http_requests_total{job="checkout",code=~"5.."}[5m]))'
  agent_factory_logql: '{namespace="checkout",app="checkout-service"} |= "panic"'
```

## 9. Testing

Pure, no network, no `gh`:

- Fingerprint stability: pod churn, replica hash, label reordering collapse to
  one fingerprint; a different namespace or alert name does not.
- Gate table: severity, hourly cap, open-run cap, dedup window, class.
- Routing table: label → map → default → `unrouted`.
- Issue-body golden files from recorded Alertmanager payloads.
- Redaction: a planted `password=` and a secret-shaped value are stripped.
- Truncation: an over-budget body loses bytes by source, not structure.
- Verifier decision table: merged/not, recovered/not, recurred/not.
- Priority ordering: incidents before features; feature round-robin unchanged.
- ConfigStore migration: the incident pipeline seeds once and stays deleted.

Existing commands gate every PR:
`dotnet build AgentFactory.slnx`, `dotnet test AgentFactory.slnx`,
and for board work `pnpm lint && pnpm typecheck && pnpm test && pnpm check`.

## 10. Risks

| Risk | Mitigation |
|---|---|
| Alert storm → runaway spend | Hourly ticket cap, open-incident cap, priority lane, fleet pause as the kill switch |
| Duplicate tickets on webhook retries | Durable inbox + fingerprint index; hidden marker in the body |
| Duplicates after a PVC wipe | Startup reconcile: list open issues carrying the marker before creating |
| Wrong repo routing | Explicit annotation first, dry-run week, `unrouted` recorded rather than guessed |
| Agent "fixes" infrastructure | Class gate; capacity/infra never auto-run |
| Secrets leak into an issue | Read-only evidence, no `describe secret`, redaction list, byte caps |
| Recurrence thrash | Incident memory; escalate after two recurrences after merge |
| Stale evidence | Snapshot at detection time, both to the issue and the PVC |

## 11. Definition of done for the whole effort

1. One alert lands as exactly one labelled issue.
2. A repeat within the window creates zero new issues.
3. The issue body alone is enough to diagnose without cluster access.
4. The run opens a PR whose new test fails before and passes after.
5. Merge + rollout clears the alert; the verifier closes the issue with
   before/after numbers.
6. A recurrence after merge comments and escalates; it never re-fixes blindly.
7. Dry-run, caps and the fleet pause are all exercised and visible on the board.

## 12. Open questions

- Which detector is authoritative today — Prometheus/Alertmanager, Grafana
  Alerting, or a SaaS (Datadog, New Relic, Dynatrace)? Only the webhook
  normalization in M1 changes; everything downstream is identical.
- Are the workloads GitOps-managed (Argo CD / Flux)? This decides whether the
  verifier can key off rollout status or must poll PR merge + a fixed window.
- Who owns `agent_factory/repo` on the manifests — this is the one annotation
  every workload needs, and a missing one is a silently unrouted signal.
