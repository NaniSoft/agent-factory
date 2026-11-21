# Research: opencode's skill system and scoping skills per session

Ticket: [issues/009-research-opencode-skills.md](../../issues/009-research-opencode-skills.md)
Engine verified: opencode 1.18.31 (installed on this machine). Docs fetched
from opencode.ai on 2026-10-24 track a newer version; behaviors below were
cross-checked against the local binary where possible, and anything
version-sensitive is flagged in **Open risks**.

## Recommended mechanism

**Restrict, don't provision: per-worktree `opencode.json` gains a
`permission.skill` map that denies everything then allows exactly the phase's
skills — supplemented (optionally) by per-message `agent` selection.**

```json
{
  "permission": {
    "edit": "allow",
    "bash": "allow",
    "skill": { "*": "deny", "tdd": "allow" }
  }
}
```

- The host already writes this file per worktree (ADR 0002); it only grows a
  `skill` key. Evaluation is **last-matching-rule-wins** (NOT
  most-specific-wins): the catch-all `"*": "deny"` must come **first**, each
  allowed skill after it — exactly the shape above.
- **No skill injection is needed for the MVP**: `~/.agents/skills/<name>/`
  (where this machine's `tdd`, `pr`, `code-review`, … live) is a native
  opencode discovery path ("agent-compatible"), so every session already sees
  the user's whole library; the per-worktree permission map is what carves the
  phase's subset out of it.
- Optional second lever: `POST /session/:id/message` accepts **`agent`** (and
  `model`) per message. Phase-specific agents can be declared in the same
  worktree `opencode.json` under `agent.<name>` with their own
  `permission.skill` (agent rules take precedence over global), and the phase
  session started with that agent — useful when phases should also differ in
  model or system prompt, not just skills.
- Belt-and-braces: the phase prompt still says "use the `tdd` skill" —
  prompt-instruction alone is the weakest guarantee and is **not** the
  mechanism, just a nudge on top of it.

Anti-goal respected: this is playbook steering, not security (ADR 0002
posture unchanged — bash runs as the user either way).

## Findings

### 1. Skill definition and discovery

- A skill = folder + all-caps `SKILL.md`; required frontmatter `name`
  (lowercase, hyphens, **must match the directory name**) and `description`;
  optional `license`, `compatibility`, `metadata`. Verified locally:
  `~/.agents/skills/tdd/SKILL.md` carries exactly `name: tdd` +
  `description: …`.
- Discovery locations (directory always plural `skills/`):
  - Project: `.opencode/skills/<name>/SKILL.md` — opencode **walks up from
    cwd to the git worktree root**, so a skill at a run-worktree root is found.
  - Global: `~/.config/opencode/skills/<name>/SKILL.md` (exists on this
    machine; currently holds cloudflare-related skills).
  - Claude-compatible: `.claude/skills/` and `~/.claude/skills/`.
  - **Agent-compatible: `.agents/skills/` and `~/.agents/skills/`** — the
    user's skill library is engine-visible as-is.
  - Skills are **not** loadable from plugins.
- Local global config `~/.config/opencode/opencode.jsonc` is only a
  `$schema` reference — no skill/permission overrides in effect globally.

### 2. Invocation

- One tool: `skill`, called as `skill({ name: "tdd" })`. Available skills are
  advertised to the model in the tool description via an
  `<available_skills>` XML block; content loads on demand.
- No slash commands, no automatic selection from metadata (opencode 1 does
  not use metadata to control selection).

### 3. Scoping — the core question

- `permission.skill` in config accepts `allow | ask | deny` or a
  glob→action map (`"internal-*": "deny"`), wildcards `*`/`?` supported.
- Precedence: **rules are evaluated by pattern match with the last matching
  rule winning** — specificity does not decide; order does. Recommended
  style (and the required shape for us): catch-all first, specifics after.
- Per-agent scoping exists both ways: custom agents via markdown frontmatter
  or `opencode.json` `agent.<name>.permission` (agent rules **take
  precedence over** global config), and `tools: { "skill": false }` disables
  the tool entirely (omits `<available_skills>`).
- What `deny` does for a skill: the skills doc says hidden/rejected (the
  permissions doc only says "block"); whether the skill disappears from
  `<available_skills>` or the call is rejected at invocation is unverified —
  either way the session cannot use it (see Open risks).

### 4. Host injection

- The host can materialize skills into a worktree at
  `.opencode/skills/<name>/SKILL.md` before session start; the walk-up
  discovery covers the worktree root. Untracked files don't land in PRs
  unless the agent stages them — same exposure as today's `opencode.json`,
  so the same untrack-before-PR cleanup applies if injection is ever used.
- Whether a long-running `opencode serve` picks up newly materialized files
  **without restart** is not documented. Strong indirect evidence yes: v1
  proved per-worktree `opencode.json` permissions take effect per session on
  the shared server (ticket 005), implying per-session config resolution;
  skill discovery rides the same config path but deserves the one live probe
  listed below.

### 5. API levers on the session/message payloads

- `POST /session` body: only `{ parentID?, title? }` — **no** agent/model at
  session create.
- `POST /session/:id/message` body: `{ messageID?, model?, agent?, noReply?,
  system?, tools?, parts }` — **per-message agent and model selection** is the
  API lever; also present on `/command` and `/shell`.
- `GET /agent` lists agents. **Correction (2026-10-24, live probe for ticket
  012): a skills endpoint DOES exist** — `GET /skill` takes `directory` and
  `workspace` query params and returns every skill discovered for that
  directory (name, description, slash, location, content). Verified on 1.18.31
  against a scratch directory: the response included the whole
  `~/.agents/skills` library plus a skill materialized on disk after the
  server started. The UI's skill picker can therefore enumerate from the
  engine — exactly the set sessions will discover — though reading the
  host-side library directory remains a valid fallback.
- `PATCH /config` can update a running server's config — a fallback if
  per-directory resolution ever proves insufficient.

## Open risks (for ticket 012 / the build ticket)

All three settled by a live throwaway probe on 2026-10-24 (ticket 012):
scratch dir + `opencode.json` (`edit`/`bash` deny; `skill`: `"*": "deny"`,
`tdd` + `probe-skill` allow), fresh session against a shared `opencode serve`,
then teardown.

1. **deny-vs-hidden semantics — SETTLED: deny hides.** Asked to list its
   advertised skills, the session answered exactly `probe-skill, tdd` — the
   permission map prunes `<available_skills>` itself; denied skills never
   reach the model. Phase prompts may freely mention what a phase denies.
2. **Hot discovery on a shared server — SETTLED: works, no restart.** The
   `probe-skill` folder was written *after* `opencode serve` started and was
   both listed by `GET /skill?directory=…` and advertised to the session
   created afterwards.
3. **Version drift — SETTLED against the local OpenAPI spec (`GET /doc`).**
   `POST /session/:id/message` body carries `agent: string` and
   `model: object`; `PermissionConfig`'s tool keys include `skill`, each rule
   being `allow|deny|ask` or a glob→action map — exactly the shape this doc
   recommends. Bonus: `GET /skill` exists (see §5 correction).

## Sources

- https://opencode.ai/docs/skills/ — definition, discovery paths, invocation,
  permission.skill examples
- https://opencode.ai/docs/permissions/ — key list, last-match-wins
  precedence, per-agent overrides, defaults
- https://opencode.ai/docs/agents/ — agent fields, `permission` vs deprecated
  `tools`, built-ins (`build`, `plan`, `general`, `explore`)
- https://opencode.ai/docs/server/ — session/message body fields,
  `GET /agent`, `PATCH /config`, experimental tool endpoints
- Local: `opencode --version` (1.18.31), `opencode --help`, inspection of
  `~/.config/opencode/` and `~/.agents/skills/` (read-only)
