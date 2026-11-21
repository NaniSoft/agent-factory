# Agent Factory

Canonical vocabulary for Agent Factory, the C# agent-fleet orchestrator with
opencode as its engine. Definitions only — no implementation detail.

## Language

**Agent Factory**:
The product: an autonomous agent-fleet orchestrator that turns GitHub issues
into merged intent — agent sessions implementing them and PRs landing the work.
_Avoid_: Shipwright (the original this recreates).

**Engine**:
The coding agent Agent Factory drives to do implementation work. Which engine a
session runs on is fixed by its flavour — the single kept flavour (`opencode-go`)
drives opencode.

**Flavour**:
Which coding agent — and credential mode — a profile drives: `opencode-go`
(the opencode model-access product behind the one opencode CLI). A closed set
of one: a new flavour arrives as code, not config. (Zen, Claude and Copilot
flavours were dropped; configs carrying them drop at load.)
_Avoid_: Provider (opencode's own term for its model sources), kind, type.

**Session**:
One conversation between the orchestrator and the engine: the engine working on
a single task. Has a lifecycle (started, working, finished, failed) and a log.

**Run**:
The orchestrator's end-to-end handling of one unit of work: pick up an
intake-eligible issue, walk its pipeline (one engine session per phase), land
the work as a PR, record the outcome. A child run is a run like any other,
spawned and ordered by its parent.
_Avoid_: Job, task (a *task* is what the issue asks for; the run is the handling).

**Fleet**:
All sessions and runs the orchestrator is managing, seen together. What the
dashboard shows.

**Playground repo**:
The first fleet target (`demofactory-playground`), the v1 MVP's single repo:
opted-in issues arrived there, PRs landed there.

**Fleet target**:
A repo — or a whole org, covering every eligible repo in it — that the fleet is
enrolled to work on. Carries per-target settings: intake rule, pipeline
assignment, and enabled/disabled. An individually enrolled repo overrides its
org's settings; the org scan never works a repo twice.
_Avoid_: Watch, source, subscription.

**Enrollment**:
The act of making a repo or org a fleet target. Un-enrolling stops intake and
cancels queued runs but never aborts in-flight work or deletes history.

**Org scan**:
The periodic re-list of an enrolled org's repos (snapshots diffed by name)
that enrolls newly eligible repos and un-enrolls repos that left the eligible
set — skipping archived repos, forks, repos with issues disabled, and the
org's `.github`. Org settings copy into a target when it is discovered.

**Fleet pause**:
The fleet-wide kill switch: dequeueing stops (queued work waits), in-flight
runs complete, scanning continues. Distinct from disabling one target.

**Intake rule**:
How an issue on a fleet target becomes fleet work: either the **opt-in label**
(the default — a human labels the issue) or **all open issues** (worked
oldest-first; the backlog existing at enrollment is capped). Per-target
setting.
_Avoid_: Trigger mode, gate.

**Opt-in label**:
The `agent-factory` label. Under `label` intake (the default rule) the only way
an issue becomes fleet work; humans add it, and re-adding it re-triggers a run.
_Avoid_: Trigger, tag.

**Triage session**:
The cheap engine session run at intake on org-enrolled targets that decides
whether an issue is single-repo work or a cross-repo ticket, and names the
repos involved. Its verdict is recorded on the run; human labels
(`agent-factory:here`, `agent-factory:cross`) override it either way.

**Cross-repo ticket**:
An issue whose work spans multiple repos of an org. Routed into the org's
`.github` repo as a new labelled issue carrying the original verbatim; the
original is closed as a duplicate linking back. Worked as a parent run.
_Avoid_: Multi-repo issue, epic.

**Parent run**:
The run on a cross-repo ticket: one planning session turns the issue into
ordered child-run specs, then the orchestrator drives the children and tracks
their PRs on the `.github` issue. Done when every child is green.

**Child run**:
An ordinary single-repo run spawned and ordered by a parent run — own worktree,
pipeline, and PR. Children run sequentially, each basing on the previous
child's branch (stacked PRs, merged in order by humans).

**Pipeline**:
The ordered phases a run walks from issue to finished work. Defined and
edited in the dashboard; each phase carries its own prompt template, skill
set, and optional agent-profile pin. The single-session run is the
degenerate one-phase pipeline.

**Phase**:
One step of a pipeline: a single engine session (a turn) in the run's
worktree, with its own prompt template, skill set, and optional pin of an
agent profile and model — both together, or neither, in which case the
session runs on engine defaults as before.
_Avoid_: Stage, step.

**Agent profile**:
A named configuration for driving one flavour: an optional credential
(see below; blank = the agent's own stored login wins) and a model palette.
Referenced by phase pins and the utility binding; several profiles may
share a flavour.
_Avoid_: Preset, key set (the profile may carry a credential, not just a
key); bare "agent" (opencode's internal concept of the same name is
unrelated).

**Credential**:
What a profile carries for the engine's auth: a static API key or an opencode
OAuth envelope (`{type:"oauth", …}`), or a `{env:NAME}` reference resolved
against the environment at injection (the Secret path: pod env → profile →
per-run ensure; ticket 042). The shape is sniffed from the value itself, so
one masked key field serves all of them. Blank = native login wins.
_Avoid_: key (a credential may be an envelope or a reference, not only a
key), token.

**Credential ensure**:
The run-start step that makes the serve able to execute on a profile's
credential (ticket 042): PUT it into the opencode credential store, and
restart the serve if it did not boot with that exact credential — auth,
like the palette, is boot-time; a PUT after boot changes nothing for the
running serve until a fresh boot bakes it. Steady state (unchanged
Secret) is PUT-only. Executions on an unbaked provider fail silently
(empty reply, zero tokens, no error part) — the run fails fast on that
shape since 042.

**Model palette**:
The multi-select set of models an agent profile offers to pipelines, drawn
from what that flavour's credentials can actually drive. Bounds every phase
pin: a pinned model must come from its profile's palette.

**Utility binding**:
The fleet-level profile-and-model that triage sessions and parent planning
sessions run on, instead of any phase's pin. Unset, those sessions run on
engine defaults.

**Fallback binding**:
The fleet-level profile-and-model that gives unpinned phases a deterministic
model (instead of the engine default, which can differ between attempts) and
gives a dead pinned model somewhere to land. Unset, unpinned phases run on
engine defaults and a dead pin fails the run.

**Model-health probe**:
The tiny bounded session — a one-word reply — the fleet runs against a model
through the real serve before betting a phase or triage session on it. Alive
means a non-empty reply with output tokens; a silent empty reply or a timeout
means the model is dead and the run falls back or fails fast, never hangs.

**Skill set**:
The skills a phase's session may use, enforced by the engine's permission
config over the discovered skill library — playbook steering, not sandboxing.

**Skill library**:
The host-side collection phases draw their skill sets from. One directory
the engine natively discovers, so what the picker offers and what a session
can use are the same set.

**Initial prompt**:
The editable per-phase template wrapped around an issue: role and context
preamble, the issue verbatim via placeholders, then definition of done and
constraints. Never a bare issue body.
_Avoid_: Run prompt (retired).

**Definition of done**:
What finishes a run: a PR opened, with build and tests verified green by the
agent itself as its final step. Per-target build/test commands render into
the prompt via `{{definition_of_done}}`.

**Worktree**:
A run's private checkout of the playground repo; the only place that run's
agent may work. Scopes the repo blast radius — not a machine sandbox.

**Poller**:
The component that watches enrolled fleet targets for intake-eligible issues
and enqueues them, round-robin across targets.

**Dashboard**:
The web UI: live sessions, per-session logs, run history, and the writable
configuration surfaces — pipelines and fleet targets. Run views are read-only
by decision.

**Deployment**:
The fleet packaged as one all-in-one pod on Kubernetes (v4): the image
carries the app, opencode, gh, git, and the skill-library snapshot;
static Fleet settings come from the ConfigMap, keys from the Secret
(ADR 0005), and durable state from the single PVC. One instance runs the
loop; a second fleet anywhere else is a duplicate-intake hazard, not a
scale-out.
_Avoid_: Cluster (the substrate, not the fleet's packaging), image (one
layer of it).

**Config store**:
Where the fleet's runtime-mutable configuration lives: pipelines, fleet
targets, the default pipeline assignment, agent profiles, the utility
binding, and the fleet paused flag. Edits apply to future runs only; an
in-flight run keeps the pipeline — and pins — it started with.
_Avoid_: appsettings (machine-local infrastructure config, restart-required).

**Integration contract**:
The agreed way the C# host starts, observes, and collects results from engine
sessions (server API first, CLI fallback).
