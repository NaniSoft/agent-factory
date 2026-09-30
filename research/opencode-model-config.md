# How OpenCode receives its model and credential in a round

Research for tracker issue #29. The worker image pins **opencode 2.0.18**, standalone
Linux build, running as unprivileged user `agent` with no human in the container.

**Method.** Everything under "Measured on the pinned binary" was established by
downloading `opencode-windows-x64.zip` from the same distribution channel the worker
image uses (`https://opencode.ai/files/bin/2.0.18/…`), unpacking it, and running the
commands below against a throwaway `HOME` with no `auth.json`, no SQLite database and no
saved accounts. Every authenticated-looking failure below is an upstream 401 from the
provider itself, which is what makes it evidence: opencode reached the provider and the
provider rejected a key that came from nowhere but the environment. The Windows build is
the same 2.0.18 the image ships; the Linux flag surface itself is **not** separately
verified here (see Unverified).

## The answers in one paragraph

`opencode run` in 2.0.18 takes the model as `--model`/`-m` in the form
`provider/model#variant`; a bare provider name is rejected (`Invalid model reference:
anthropic`), so the project file's `llm.provider: anthropic` cannot be passed through as
is — something has to expand it to a full model id. Credentials do **not** require
`opencode auth login`: each provider's canonical environment variable (`ANTHROPIC_API_KEY`,
`OPENAI_API_KEY`, …) is read straight off the server process and shows up in `auth list`
with type `environment`; only a *saved* account outranks it, and this image has none. The
model can come from a committed `opencode.json` in the round's tree, but the tree is
untrusted and its config also controls the provider endpoint, so the factory should pass
`--model` itself and inject the key under the provider's canonical variable name — which
keeps the worker image credential-free exactly as it is today. `--standalone` is real and
load-bearing: without it, `run` starts a **shared background service** that outlives the
CLI and holds a loopback port; with it, the run is a private child server and
`opencode service status` stays `stopped`.

---

## 1. The non-interactive invocation

### 1.1 The exact flag surface, from the pinned binary

`opencode run --help`, opencode 2.0.18 (verbatim):

```
USAGE
  opencode run [flags] [<message...>]

FLAGS
  --standalone            Run with a private server instead of the background service
  --server string         Connect to a server URL instead of the background service
  --continue, -c          Continue the last session
  --session, -s string    Session ID to continue
  --fork                  Fork the session before continuing
  --model, -m string      Model to use in the format provider/model#variant
  --agent string          Agent to use
  --format choice         Output format (choices: default, json)
  --file, -f string       File to attach to the message
  --title string          Session title
  --thinking              Show thinking blocks
  --auto                  Auto-approve permissions that are not explicitly denied
```

Global flags worth knowing: `--log-level <…>`, `--print-logs` ("Print logs to stderr
(server logs require `--standalone")`), `--version`, `--completions`.

All three flags the factory passes today exist in 2.0.18 — `--standalone`, `--auto`,
`--file` — and the message is accepted after a `--` separator (measured: the same
"reached the provider" failure comes back with `-- "say hi"`). `--file` is a single
string in this version, not repeatable.

Source: `opencode run --help` on the 2.0.18 binary, downloaded from
<https://opencode.ai/files/bin/2.0.18/opencode-windows-x64.zip> — the same
`/files/bin/<version>/` channel `worker/Dockerfile` pins for `opencode-linux-x64.tar.gz`.

### 1.2 What the factory runs today, and the one gap in it

`src/agent-factory/Rounds/WorkerRoundRunner.cs`, `RoundScript`:

```
opencode run --standalone --auto --file "$out/brief.md" -- "$AGENT_FACTORY_AGENT_PROMPT"
```

There is **no `--model`**. With no `-m` and no config, opencode picked a model by itself
(measured: with only `ANTHROPIC_API_KEY` set and no `-m`, the request went to Anthropic's
default — "API key is invalid" came back from Anthropic, so a default model *was*
resolved). That means the model a round uses today is whatever opencode's built-in
default resolves to, not what the project file says. `llm.provider` is loaded and logged
(`ProjectFileLoader.cs`) but reaches nothing downstream of it — `Project.cs` carries
`LlmProvider` and `WorkerRoundRunner` reads only `project.LlmKeyName`.

### 1.3 Mapping `llm.provider` to a `provider/model` identifier

`-m` is parsed by splitting on `/` and requires both halves. Measured:

| invocation | result |
| --- | --- |
| `-m anthropic` | `Error: Invalid model reference: anthropic` (exit 1) |
| `-m anthropic/claude-sonnet-4-5` | request sent to Anthropic (401 for a dummy key) |
| `-m openai/gpt-5` | request sent to OpenAI |
| `-m` omitted | opencode's own default model resolved and used |

So `llm.provider: anthropic` has to become e.g. `anthropic/claude-sonnet-4-5` before it
reaches the CLI. Two places that mapping can live:

- **in the factory** — a provider → default-model table next to `Project`, producing
  `-m "$provider/$model"`. Visible in the round's own command record, override-able
  nowhere else.
- **in the project file** — add `llm.model` beside `llm.provider` and pass the whole
  string through. No table to keep current as providers deprecate model ids, at the cost
  of one more schema field (`ProjectFileLoader` refuses unknown fields by design, so this
  is a deliberate schema change, not a quiet one).

Either is fine; the second is the only one that survives model deprecation without a
factory release. What does *not* work is passing `llm.provider` through unchanged.

`--model` beats config: with a repo `opencode.json` setting `"model":
"anthropic/claude-sonnet-4-5"` and `-m openai/gpt-5` on the command line, the request
went to **OpenAI** (measured). The flag wins.

### 1.4 `--format json`

`--format json` emits one JSON object per event (`tool_use`, `step_start`, `step_finish`,
`text`, `reasoning`, `error`) rather than formatted prose. Unverified against a live
model here — no real key was available — so treat the event shape as undocumented rather
 than wrong. Source: `opencode run --help` (choices `default, json`).

---

## 2. Authentication

### 2.1 Yes, there is a documented env-var-only path, and it works

The v2 provider docs describe an "Environment" method: it "Reads a supported variable
from the server process **without saving it**", and "Environment connections appear in
`auth list` with type `environment`". Measured on 2.0.18 in a home with no saved
accounts:

```
$ opencode auth list
Anthropic  ANTHROPIC_API_KEY           environment
```

That line is opencode reporting the environment variable itself as a credential source —
which is the whole answer to "does the credential have to arrive as `auth login` state".

The empirical proof, both directions:

| environment | invocation | result |
| --- | --- | --- |
| `ANTHROPIC_API_KEY=sk-dummy-ant` only | `run --standalone -m anthropic/claude-sonnet-4-5` | Anthropic's own 401: `API key is invalid.` (exit 1) |
| `OPENAI_API_KEY=sk-dummy-openai` only | `run --standalone -m openai/gpt-5` | OpenAI's own 401: `Incorrect API key provided: sk-dummy***enai.` (exit 1) |
| no key at all | same `-m anthropic/claude-sonnet-4-5` | `Error: Model unavailable: anthropic/claude-sonnet-4-5` (exit 1) |

The third row is the one that matters for the factory: an unauthenticated provider is not
an error message about keys, it is the model disappearing from the catalog. And the exit
code is `1` in all three cases, so the factory cannot distinguish "rate limit, retry me"
from "credential wrong, stop asking" from `roundExitCode` alone.

### 2.2 Which variable, per provider

The variable name is the provider's own canonical one, carried in opencode's provider
catalog — not a name the project file chooses. Confirmed for `ANTHROPIC_API_KEY` and
`OPENAI_API_KEY` by measurement; the 2.0.18 binary also contains the strings
`OPENROUTER_API_KEY`, `GOOGLE_GENERATIVE_AI_API_KEY`, `GROQ_API_KEY`, `XAI_API_KEY`,
`AWS_ACCESS_KEY_ID`. Bedrock uses the AWS credential chain (plus
`AWS_BEARER_TOKEN_BEDROCK`) and Vertex uses Application Default Credentials plus
`GOOGLE_CLOUD_PROJECT` — those two are documented rather than measured here.

Consequence for this repo: the project file names the key
(`keys.llm: NEXUS_ANTHROPIC_API_KEY`) and `WorkerRoundRunner.EnvironmentFor` injects the
value under **that** name. opencode will not find it. For `llm.provider: anthropic` the
value has to arrive as `ANTHROPIC_API_KEY`. Two structural fixes, in increasing order of
faithfulness to the current schema:

1. Rename in the project file: `keys.llm: ANTHROPIC_API_KEY`. Zero code change, and the
   existing log line ("the name, never the value") still reads true. Costs the look of a
   factory-owned variable name in a project file.
2. Keep the project's name and have the factory pass the value through under the
   provider's canonical name as well (one line in `EnvironmentFor`, using
   `project.LlmProvider` to pick it). The project file keeps its own vocabulary.

`OPENCODE_API_KEY` exists but is **not** a universal fallback: in the bundled code it is
read only for the `opencode` provider (opencode's own gateway). Don't reach for it as a
generic key slot.

### 2.3 `auth login` state, and what "auth.json" means in 2.x

`opencode auth login` writes a saved account, and "a saved account takes precedence over
an environment connection for the same integration" (v2 provider docs). So a stale saved
account in the image would silently beat the round's fresh credential — one more reason
the image is right to ship none. `worker/Dockerfile` bakes no credential and creates
`agent`'s home fresh, so there is nothing to go stale.

Where state lives (all confirmed by watching what 2.0.18 created under a throwaway
`HOME`):

| path | what |
| --- | --- |
| `~/.local/share/opencode/opencode.db` (+`-shm`/`-wal`) | sessions and saved accounts (SQLite) |
| `~/.local/share/opencode/auth.json` | **legacy** credential file; v2 imports it during migration |
| `~/.local/share/opencode/log` | logs |
| `~/.local/state/opencode/service.json` | where the running background service advertises its URL |
| `~/.config/opencode/` | global config, `AGENTS.md`, `service.json` |
| `~/.cache/opencode/bin` | cache |

`XDG_DATA_HOME`, `XDG_CONFIG_HOME`, `XDG_CACHE_HOME`, `XDG_STATE_HOME` are all honored by
the binary. `/home/agent` is therefore the only thing a round needs to be writable, which
the image already guarantees.

A saved account can also be created non-interactively — `opencode auth login <target>
--method <id> --answer key=value` — but that writes durable state into the image's home
and is strictly worse than an environment variable for a one-round container. Not needed.

---

## 3. Where the model config lives

Three places, all verified on the pinned binary:

| place | honored? | who authors it |
| --- | --- | --- |
| `--model` on the command line | yes, and it **overrides** config | the factory |
| `opencode.json` in the round's tree | yes — `"model": …`, `"provider": {…}` read from the working directory | the project's repository |
| `OPENCODE_CONFIG` / `OPENCODE_CONFIG_CONTENT` env vars | yes | whoever starts the container |

Details that decide it:

**The tree's `opencode.json` is read and is authoritative over nothing the factory
passes.** With `opencode.json` in the working directory containing `"model"` and
`"provider": {"anthropic": {"options": {"apiKey": "{env:ROUND_TEST_KEY}"}}}`, and *only*
`ROUND_TEST_KEY` in the environment, the request went to Anthropic. So `{env:VAR}`
substitution works in 2.0.18, and so does a project-committed config.

**But the tree's config also controls where the credential goes.** The same
`opencode.json` with `"provider": {"anthropic": {"options": {"baseURL":
"http://127.0.0.1:9/"}}}` sent the request to that URL instead of Anthropic (transport
error against the dead port; the key never got a chance to be judged). A round tree is
untrusted code by the factory's own admission (`worker/README.md`), so a committed config
can point the provider at an endpoint of the tree author's choosing and read the
credential off the request. It adds no capability the agent does not already have — the
credential is in the agent's environment and the agent runs arbitrary shell — but it is
the difference between "the agent could leak it" and "a file the reviewer is diffing
leaks it silently".

**opencode can be told to ignore the tree's config.** The same redirecting `opencode.json`
plus `OPENCODE_DISABLE_PROJECT_CONFIG=1` in the environment went to Anthropic instead —
the project config was not applied. This variable exists in the 2.0.18 binary and does
what its name says (measured).

**`OPENCODE_CONFIG` works for config that lives outside the tree.** A config file at an
arbitrary path, named by `OPENCODE_CONFIG`, was honored (`model: openai/gpt-5` picked up
from `/tmp/…/cfg/opencode.json`). `OPENCODE_CONFIG_CONTENT` (inline JSON) and
`OPENCODE_CONFIG_DIR` are also present as strings in the 2.0.18 binary; only
`OPENCODE_CONFIG` was measured end to end.

### Recommendation

Pass the model as `-m` from the factory, and inject the credential under the provider's
canonical variable name. Concretely, for `llm.provider: anthropic`:

```
ANTHROPIC_API_KEY=<project's keys.llm value> \
opencode run --standalone --auto \
    -m anthropic/<model> \
    --file "$out/brief.md" -- "$AGENT_FACTORY_AGENT_PROMPT"
```

and consider `OPENCODE_DISABLE_PROJECT_CONFIG=1` alongside it if the factory wants
"which endpoint, which model" to be a property of the project file and the factory rather
than of whatever the round's tree happens to commit.

This keeps the worker image credential-free, which is already true and needs no change:
`worker/Dockerfile` bakes no key, `WorkerRoundRunner` is the single place a credential
value enters a container, and the result records `credentialEnvNames` so a reviewer sees
the *names*. Nothing about the env-var path above asks the image to hold anything.

---

## 4. The background service, and what `--standalone` does about it

The worker README says opencode "starts a background service that binds a loopback port
inside the container". That is correct and worth understanding precisely, because in 2.x
the service is the *default*, not a side effect:

> "By default, OpenCode discovers or starts one shared background server per user
> account; all local clients connect to it, and it owns sessions, configuration,
> integrations, permissions, and tool execution." — <https://opencode.ai/v2/docs/cli>

Measured, in a fresh `HOME`:

- `opencode service status` → `stopped`.
- `opencode run "say hi"` (no `--standalone`) → runs, and afterwards `opencode service
  status` → `http://127.0.0.1:49374`. The service **outlived the CLI** and advertised
  itself from `~/.local/state/opencode/service.json`.
- `opencode run --standalone "say hi"` in a fresh home → runs, and afterwards `opencode
  service status` → still `stopped`. Nothing persisted.
- What `--standalone` actually spawns, from opencode's own log line:
  `"spawning process" … args=["serve","--stdio","--port","0"]` — a private child server,
  spoken to over stdio, on an OS-assigned port.

So the README's advice is right and is the difference between two shapes:

| | without `--standalone` | with `--standalone` |
| --- | --- | --- |
| who owns the session | the shared service, which survives the CLI | a child process of this `run` |
| listening socket | one, on loopback, **left behind** | one, on loopback, **dies with the run** |
| risk in a round | a second round in the *same* container could attach to the first round's service and its sessions | none; the round's process tree ends with it |

One round is one container here, so the cross-round hazard cannot arise — but the
collector records any listening socket it finds, and without `--standalone` it would
rightly report one on every round. Keep `--standalone`.

There is also a documented way to make the private server the default for the image
rather than a per-invocation flag: `opencode service set disabled true` (undo with
`opencode service unset disabled`), after which `--server <url>` still works. That is an
image-level decision; the flag the factory already passes is enough, and baking the
`service set` state into the image would be one more thing a derived project image has to
remember to inherit.

`--server <url>` connects to an explicit URL instead of either of the above — the seam a
future design would use if a round were ever to drive a server the factory started
deliberately. Nothing today needs it.

---

## 5. How this connects to the code as it stands

- `worker/README.md` — its background-service paragraph and its `--standalone` advice are
  both confirmed by measurement; nothing there needs correcting.
- `worker/Dockerfile` — pins 2.0.18 by checksum from the same URL scheme measured here;
  creates `/home/agent`, which is the one writable path opencode needs.
- `src/agent-factory/Rounds/WorkerRoundRunner.cs` — `RoundScript` is correct in its flags
  and silent on the model; `EnvironmentFor` is the single credential seam and currently
  hands the key over under the project's own name rather than the provider's.
- `src/agent-factory/Projects/Project.cs` / `ProjectFileLoader.cs` — `LlmProvider` is
  loaded, logged and then used by nothing; it is the natural input to the `provider/model`
  expansion.
- `factories/nexus.yaml` — `llm.provider: anthropic` + `keys.llm: NEXUS_ANTHROPIC_API_KEY`
  is the concrete pair that needs the two changes above.

One operational note: every failure mode measured here exits `1` — invalid key, missing
key, unreachable endpoint, unknown model reference. `roundExitCode` therefore cannot
separate "retryable" from "the credential is wrong"; if the factory wants that split it
has to read the round's own log text, not the number.

---

## Unverified

- **The Linux build's flag surface.** Everything measured was the Windows x64 build of
  the *same version* the image pins. The Linux binary comes from the same release channel
  and the same CLI source, but `opencode run --help` was not run inside the worker image.
  Cheap to confirm: `docker run --rm ghcr.io/nanisoft/agent-factory-worker:1 exec opencode
  run --help`.
- **`--format json` event shape.** Documented as a choice, not exercised against a live
  model. Any consumer the factory writes against it should be written from a real capture,
  not from this document.
- **Provider env vars other than `ANTHROPIC_API_KEY` / `OPENAI_API_KEY`.** Their presence
  in the binary and the general "supported variable per provider" rule are documented; the
  per-provider list (Google, Groq, xAI, OpenRouter, AWS) was not exercised.
- **`OPENCODE_CONFIG_CONTENT`, `OPENCODE_CONFIG_DIR`.** Present in the 2.0.18 binary and
  documented on the 1.x config page; not measured end to end here (only `OPENCODE_CONFIG`
  was).
- **`opencode service set disabled true`.** Documented on the v2 CLI page; not measured.
- **The 2.x source tree.** `sst/opencode` now redirects to **`anomalyco/opencode`**;
  current `master`'s `run.ts` has *no* `--standalone` and a different flag set (`--attach`,
  `--port`, `--variant`, `--share`), i.e. the flag surface is moving between releases.
  Pin-by-version is doing real work here; a version bump deserves a re-run of this
  document's command list.

## Sources

- opencode 2.0.18 binary, `opencode run --help`, `opencode --help`, `opencode auth --help`,
  `opencode auth login --help`, `opencode models --help`, `opencode service --help`,
  `opencode auth list`, `opencode service status` — measured, downloaded from
  <https://opencode.ai/files/bin/2.0.18/opencode-windows-x64.zip>
- v2 CLI docs (background service, `--standalone`, `--server`, `service set disabled`):
  <https://opencode.ai/v2/docs/cli>
- v2 providers docs (environment method, `auth list` type `environment`, saved-account
  precedence, `auth.json` as legacy, SQLite at `~/.local/share/opencode/opencode.db`):
  <https://opencode.ai/v2/docs/cli/providers>
- Config docs (`opencode.json` discovery walking up to the nearest git directory,
  `OPENCODE_CONFIG`, `OPENCODE_CONFIG_CONTENT`, `model`/`small_model`,
  `provider.<id>.options.apiKey`/`baseURL`, `{env:VAR}`/`{file:path}`):
  <https://opencode.ai/docs/config/>
- Providers docs (auth.json path, `/connect`, `{env:ANTHROPIC_API_KEY}` example, Bedrock /
  Vertex / Azure variable names): <https://opencode.ai/docs/providers/>
- Current source, `run` command (flag set in the generation *after* 2.0.18 — no
  `--standalone`): <https://github.com/anomalyco/opencode/blob/master/packages/opencode/src/cli/cmd/run.ts>
- Current source, auth store (`auth.json` under `Global.Path.data`,
  `OPENCODE_AUTH_CONTENT`): <https://github.com/anomalyco/opencode/blob/master/packages/opencode/src/auth/index.ts>
- Upstream issue on env-var-vs-saved-account preference for Anthropic:
  <https://github.com/anomalyco/opencode/issues/7456>
