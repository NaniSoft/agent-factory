# Research: zcode's headless contract from a host process

Ticket: [issues/026-research-zcode-headless.md](../../issues/026-research-zcode-headless.md)
Engine verified: ZCode desktop 3.16.3 (Electron 41.0.3) shipping glm CLI bundle
**0.17.0**, on this machine (Windows, Git Bash, node v24.22.1). Evidence is
first-party: the minified CLI bundle itself
(`resources\glm\zcode.cjs`, built from `apps/zcode-cli/packages/cli/dist`),
the live process table, `~/.zcode` state, and **live runs** of the CLI
(flagged as such below). ZCode's public docs (zcode.z.ai) do not document a
headless CLI; the only web corroboration is an upstream feature request
repeating this research's central gap
([zai-org/feedback#907](https://github.com/zai-org/feedback/issues/907):
no `--model` flag, no model field in `--json`, filed against 0.16.9).

## Recommended adapter contract

**Drive `zcode.cjs` with system node in one-shot prompt mode; require a
one-time `zcode login zai` on the machine; parse one JSON envelope from
stdout; pin models at profile level (zcode has no per-run model flag).**

```text
# one-time machine setup (interactive browser OAuth — never from the host)
node <zcode.cjs> login zai

# per-run dispatch (verified live through step 4 below)
node "%LOCALAPPDATA%\Programs\ZCode\resources\glm\zcode.cjs"
  -p "<task text>"
  --output-format json
  --cwd "<run worktree>"
  [--mode yolo]                       # default; guarded = restrictive
  [--disallowed-tools "Bash(git *)"]  # per-run tool carve, settings untouched
  [--resume <sessionId> | -c]         # session continuity per worktree
```

1. **Entry point**: no PATH binary, by design — the CLI ships inside the
   desktop app as `C:\Users\dpven\AppData\Local\Programs\ZCode\resources\glm\zcode.cjs`
   (15 MB CJS bundle; `.node-bundle-meta.json`: `{"runtime":"electron-node",
   "entry":"zcode.cjs","source":"apps/zcode-cli/packages/cli/dist/zcode.cjs"}`).
   The desktop spawns it through its own exe as a node shim
   (`ZCode.exe …zcode.cjs app-server --stdio --surface desktop` — observed in
   the live process table), but **system node runs it directly**: `node
   zcode.cjs --version` → `0.17.0` exit 0 (live); `doctor` reports
   `process: zcode-cli`, `sea: no (optional)`, `default artifact: node-bundle`.
   Adapter should locate the bundle under `%LOCALAPPDATA%\Programs\ZCode`
   (single install root, updated in place by the desktop) and gate on
   `--version` rather than hardcoding 0.17.0.
2. **One-time auth prerequisite** (the one hard blocker found): the desktop's
   login does **not** satisfy standalone headless runs. A live
   `-p "…" --output-format json` failed in 2.2 s with
   `Error: Select a model before sending a message (traceId: …)`, exit 1 —
   standalone runs need the identity/auth-source credential entries plus a
   machine-level default model, which only `zcode login` writes (bundle:
   `persistStandaloneCodingPlanConnection` saves
   `account-provider:<pid>:identity` + `:auth-source` + per-account api-key
   into the shared credential store **and** `defaultModelSelection` into
   `~/.zcode/v2/provider_config.json`, returning `mainModel`). Today's store
   holds the desktop's OAuth entries and even the coding-plan api-key entry,
   but no `identity`/`auth-source` keys — hence the failure. `zcode login
   [zai|bigmodel] [--no-browser]` is browser OAuth (no API-key positional),
   so it is a human setup step; the adapter should health-probe (cheap `-p`
   round-trip) and surface "run `zcode login zai`" on this exact error.
3. **Invocation + working directory**: `-p, --prompt <text>` is the one-shot
   form (prompt as argv; `--attach <path>` repeatable for file attachments —
   relevant for long task text against Windows argv limits). `--cwd <path>`
   repoints the run (validated, resolved against process cwd) — this is how a
   run lands in a Agent Factory git worktree. Sessions persist per cwd:
   `--resume <sessionId>` / `-c` (latest for cwd, mutually exclusive).
   Live `skills list --json` in a temp dir confirmed clean stdout/exit-0
   behavior with cwd reported.
4. **Output parsing**: `--output-format` ∈ `text|json|stream-json`
   (validated; `--json` is the documented shortcut for `json`). `json`
   writes **one pretty-printed (2-space) JSON object + newline** to stdout:
   `{type:"result", sessionId, traceId, turnId, response, turnResponses?,
   usage?, eventCount, projection:{status, turnCount, totalTokenCount,
   contextUsed, contextWindow}}` (bundle-verbatim, `runPrompt`). `response`
   is the final turn text (plus all turn texts when several). `stream-json`
   additionally streams compact NDJSON event lines during the run
   (`createHeadlessSessionObserver`: each mapped session event = one
   `JSON.stringify` line; workflow progress maps to
   `{type:"workflow.run.progress", eventId, sessionId, seq, timestamp,
   traceId, payload}`) and ends with the same summary object. Errors go to
   stderr as `Error: <message> (traceId: <uuid>)` with **exit 1**; usage
   errors (`Unsupported --mode value: bogus. …`) verified live, exit 1;
   success paths write and exit 0. There is no cost field — token counts
   only (`usage`, `projection.totalTokenCount`).
5. **Model plumbing — the real constraint**: there is **no per-run model
   flag** (help text and parser confirm; upstream issue #907 asks for one).
   Prompt runs use `modelSelectionMode:"cli-default"` → registry's
   `configuredDefaultModelSelection` → `defaultModelSelection` in
   `~/.zcode/v2/provider_config.json` (schemaVersion 1, strict:
   `{schemaVersion:1, config:{defaultModelSelection:{providerId, modelId}}}`;
   a malformed file silently falls back to empty — verified live). Model ids
   are `providerId/modelId` with provider `account:zai-individual-coding-plan`
   (family zai) on this machine. **Palette source**: the bundled catalog
   `resources\config\provider\zcode-builtin.json` (revision-stamped; declares
   10 providerRules incl. the zhipu-account coding-plan providers with
   `builtinModelIds:["GLM-5.3","GLM-5.3-Flash"]`, api `anthropic-messages`,
   plus 83 model rules with context/modalities/variants), cross-checkable
   against the resolved active view in `~/.zcode/v2/config.json`
   (`provider."builtin:zai-coding-plan".models` keys — live-read: the same
   two models across six builtin providers). **Recommendation for 025's
   per-phase pin**: for the zcode flavour, pin one model per zcode *profile*
   (machine-level default), not per phase — per-phase switching would mean
   rewriting the machine-global `provider_config.json` per run (racy with
   the desktop and other runs). Per-session model selection exists only
   behind the app-server protocol (`runtime-model-selection` session
   entries) — an escalation path if per-phase zcode pinning becomes a hard
   requirement (ticket 032 decision).
6. **Credential injection: none — native login wins.** Provider request
   headers are built internally from the shared, **encrypted** credential
   store `~/.zcode/v2/credentials.json` (values encrypted; key names only
   were inspected: `oauth:zai:*`, `zcodejwttoken`,
   `account-provider:…:identity/:auth-source/:api-key`) via a
   `providerRuntimeHeadersPort`; the coding-plan access mode is
   `individual-coding-plan` with per-account request auth. The Profiles page
   API-key field must stay empty for zcode (exactly 025 decision 6's "native
   logins win"); `ZAI_OAUTH_ORIGIN`/`ZAI_OAUTH_CLIENT_ID`/
   `ZAI_BUSINESS_BASE_URL` env vars exist for endpoint overrides, not keys.
   For isolated provider configs there is an env pair —
   `ZCODE_BUILTIN_PROVIDER_CONFIG_FILE` + `ZCODE_PERSONAL_PROVIDER_CONFIG_FILE`
   (must be set together; defaults resolve to the app's bundled file and
   `~/.zcode/v2/provider_config.json`) — code-verified, not yet proven live
   (blocked on the same missing-login issue; test harness preserved in
   `%TEMP%\zc-probe-026\`).
7. **Permissions/skills posture**: `-p` defaults to **`--mode yolo`**
   (auto-approve; verified default constant `Gbc="yolo"` + help text
   "default: yolo for --prompt"). Only two tools are auto-allowed by the
   headless broker with the reasoning "no interactive gate exists in -p
   mode"; everything else in non-yolo modes falls through to a
   **deny-permission-broker**. So: default zcode runs are *more* permissive
   than opencode's per-worktree deny-maps; passing `--mode guarded` (or
   `plan`/`build`/`edit`) engages the deny broker for gated tools — exact
   semantics unverified (open risk). `--disallowed-tools` carves tools per
   run ("Bash(git *)" syntax; comma or space separated; run-scoped, saved
   settings untouched). **Skills carve works the 012 way**: discovery roots
   are project `.zcode/skills` + `.agents/skills` (walked up toward the git
   root) plus user `~/.zcode/skills` + `~/.agents/skills`, so placing a
   phase's skills in the run worktree scopes them; `zcode skills list
   --json` enumerates (`{cwd, skills:[{name, path, scope, source,
   qualifiedName, description, whenToUse…}], totalDiscovered}` — live: 85
   skills, matching this session's own skill list) and `skills inspect
   <name>` exists; in-process `allowedSkills` filters and profile-level
   `skills` lists also exist (agent/subagent layer). Project instructions
   from `.zcode/AGENTS.md` / `.agents/AGENTS.md`.
8. **Observability**: every headless run is a real session — it migrates and
   shares `~/.zcode/cli/db/db.sqlite` (WAL; concurrent with the desktop —
   the probe runs coexisted with this very session), appears in
   `~/.zcode/cli/log/zcode-YYYY-MM-DD.jsonl` (structured JSONL with
   `traceId`, `sessionId`, `event`, `module` — verified around the failed
   probe's traceId) and `rollout/model-io-*.jsonl` for model I/O. The
   `sessionId` in the result envelope correlates across all three. Never
   parse the sqlite db or logs for results — the envelope + worktree git are
   the contract (001's anti-contract posture, carried over).

**Anti-contract**: never write `credentials.json` or `provider_config.json`
per-run from the host (encrypted store / machine-global and desktop-shared);
never launch `ZCode.exe` bare or `zcode login` from the host (GUI/browser);
never hardcode the bundle version or absolute install path without a
`--version` gate and `%LOCALAPPDATA%` discovery.

## Findings

### 1. Where the CLI actually lives

- Process table (live, `Get-CimInstance`): the desktop app spawns its CLI as
  `ZCode.exe C:\…\resources\glm\zcode.cjs app-server --stdio --surface
  desktop`, plus `__zcode-plugin-host` children per MCP plugin. The exe
  doubles as an Electron-node shim; `zcode.cjs` is the CLI.
- `.node-bundle-meta.json` next to the bundle names the source package;
  `kuc()` in the bundle classifies runtime as `"electron"` when argv contains
  `app-server`/`agent-server`, else `"cli"`.
- System node runs the bundle standalone (live): `--version`, `--help`,
  `doctor`, `skills list --json` all clean; `doctor` explicitly reports
  `sea: no (optional)` — single-executable packaging exists but is not how
  this install ships.

### 2. Command surface (help text, live-captured)

Commands: `app-server`, `commands`, `doctor`, `login [zai|bigmodel]`,
`logout`, `plugins` (list/install/uninstall/enable/disable/update/validate/
marketplace), `skills` (list/inspect), `tui`, `version`; no command +
`-p` = headless prompt. Options: `-p/--prompt`, `--memory-bench`,
`--browser-use headless`, `--browser-executable`, `--attach` (repeatable),
`--cwd`, `--disallowed-tools`, `--toolsearch-mode auto|native|local|off`,
`--force-mcs`, `--locale`, `--mode build|edit|plan|yolo|guarded`,
`--resume <sessionId>`, `--target`/`--target-replace` (headless session
goal), `--workflow-mode disabled|onDemand|alwaysOn`, `-c/--continue`,
`--json`, `--output-format` (parsed but undocumented in help),
`--surface terminal|desktop`, `--no-browser`, `--no-color`, `--verbose`.
Slash commands include `/model [id]`, `/mode`, `/mcp`, `/fork`, `/rewind`,
`/expert`, `/dwf` (TUI-side equivalents).

- Validation rules from the dispatcher: `--resume` XOR `--continue`;
  `--surface` only with `--prompt`/`--target`/`app-server`/`agent-server`;
  `--toolsearch-mode`/`--force-mcs`/`--memory-bench` only with
  `--prompt`/`--target`; empty prompt rejected; unknown command → exit 1.

### 3. Output envelope (bundle code path `runPrompt`)

- `json`/`stream-json` end with the summary object; fields as quoted in the
  contract above. `projection.status` is the turn status; `usage` carries
  token counts when the provider reported them.
- `text` (default) prints the turn responses joined by newlines; workspace
  hook trust diagnostics go to stderr (`workspace_hooks_*` reason codes).
- Exit codes: 0 on success (all three formats), 1 on any error/usage
  failure (live-verified for usage and the model-selection failure);
  fatal bootstrap errors `process.exit(1)`. No other codes observed.

### 4. Model catalog and selection chain

- `zcode-builtin.json` (bundled, CDN-refreshable — `zcode-builtin-refresh.json`
  sibling control file, revision 33 here): `providerRules` (10) with the
  coding-plan entries `{providerId: "account:zai-individual-coding-plan",
  providerName: "Z.AI Individual Coding Plan", group: "zai-family",
  builtinModelIds: ["GLM-5.3","GLM-5.3-Flash"], access: {type:
  "zhipu-account", mode: "individual-coding-plan", accountType: "zai"},
  api: {type: "anthropic-messages", …}}`; a bigmodel family twin exists.
  Exactly one coding-plan provider per family is required (`hui`).
- Resolved active catalog: `~/.zcode/v2/config.json`
  (`provider."builtin:*"` entries with `models`, `variants`, `options`
  incl. populated `apiKey` — key material, never printed here).
- Selection: `configuredDefaultModelSelection` ←
  `modelSelectionConfigRepository.read().defaultModelSelection` ←
  personal repository file = `provider_config.json` (written by login's
  `persistStandaloneCodingPlanConnection`; empty here). Per-session
  overrides persist as `runtime-model-selection` session entries (TUI
  `/model`; app-server). Host-facing per-run override: none.

### 5. Credentials

- Store: `~/.zcode/v2/credentials.json`, JSON of key → encrypted value
  (`oauth:active_provider`, `oauth:zai:access_token`,
  `oauth:zai:user_info`, `zcodejwttoken`, coding-plan `account-provider:…`
  triple, project-key location record). Base dir relocatable via
  `ZCODE_DATA_BASE_DIR` (everything hangs off `<base>\.zcode\`).
- Request auth: `providerRuntimeHeadersPort` resolves per-provider request
  auth from the store (`individual-coding-plan` mode; identity + auth-source
  + api-key; scope fingerprints guarded by sha256 account-scope hashes).
- Desktop vs standalone: the desktop manages the account in hosted
  (`app-server`) mode — host pushes account config over the protocol
  (`syncAccountProviderConfig`; "standalone account is managed by this
  process" guard proves the two modes are mutually exclusive). Standalone
  CLI needs its own login-once (`zcode login`) — the missing piece found
  live.

### 6. Permissions and skills

- `--mode` for prompts: `build|edit|plan|yolo|guarded`, **default yolo**.
  Headless broker auto-allows exactly two internal tools with "no
  interactive gate exists in -p mode"; the base broker in non-yolo modes is
  `createDenyPermissionBroker`.
- `--disallowed-tools` parsing: `Bash(git *)` tool-level exclusion patterns,
  run-scoped; normalized into a tool disallowlist in `runtimeConfig`.
- Skills: discovery `{workingDirectory, roots}` with default roots project
  `.zcode/skills`, `.agents/skills` + user `~/.zcode/skills`,
  `~/.agents/skills` (constants `.zcode`, `.agents`, `skills` in
  `resolveDefaultSkillRoots`); `zcode skills list [--json]` and `skills
  inspect <name>`; in-process `allowedSkills` wrapper filters discovery
  results; agent definitions (`frontmatterKeys`, per-agent `model:`) exist
  for the subagent layer. Custom commands mirror the same roots with
  `commands/` (`zcode commands`).

### 7. Web corroboration (thin by design — local evidence owns the contract)

- [zai-org/feedback#907](https://github.com/zai-org/feedback/issues/907):
  heavy headless automation user on 0.16.9 requests `--model` and model in
  `--json` — confirms both the headless pattern in the wild and that the
  model-flag gap is real upstream.
- [ZCode docs](https://zcode.z.ai/en/docs) cover desktop/TUI commands only;
  [GLM coding plan quick start](https://docs.z.ai/devpack/quick-start)
  covers plan auth for agent tools; no official headless CLI reference
  exists — the bundle and this document are the reference.

## Open risks (parked for ticket 032)

1. **Login prerequisite UX**: `zcode login zai` is interactive-browser and
   machine-global; Agent Factory needs a setup check (probe run + actionable
   error) and a doc note, not automation.
2. **Per-phase model pinning**: impossible per-run today; recommend
   profile-level pin (one model per zcode profile) or invest in the
   app-server protocol (session-scoped `runtime-model-selection`).
3. **`--mode guarded|build|edit|plan` exact semantics** under `-p`
   (deny-broker behavior per tool class) unverified — probe once login is
   in place, before choosing the phase default.
4. **Version drift**: desktop auto-updates replace the bundle in place
   (0.17.0 today); gate on `--version`, re-check the envelope shape if minor
   version moves.
5. **Env override pair** (`ZCODE_BUILTIN_PROVIDER_CONFIG_FILE` +
   `ZCODE_PERSONAL_PROVIDER_CONFIG_FILE`) code-verified only; prove before
   relying on it for isolated/CI-style configs.
6. **Long prompts**: argv size limits on Windows — use `--attach` for bulky
   task text or keep phase templates small.
