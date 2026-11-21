# Research: opencode flavours — key injection, model catalogs, per-session pinning

Ticket: [027-research-opencode-flavour-auth-and-catalogs](../../issues/027-research-opencode-flavour-auth-and-catalogs.md)
Date: 2026-10-28. Engine probed live: `opencode 1.18.31` (the established
server-first contract of [ticket 001](../../issues/001-research-opencode-api.md)
is the baseline; nothing below contradicts it). Sources are marked:

- **[live]** — probes against a live `opencode serve` this session. All
  serve-API experiments ran in an **isolated sandbox** (`XDG_DATA_HOME` /
  `XDG_CONFIG_HOME` pointed at a temp dir — verified to fully redirect the data
  and config roots on Windows) on ephemeral ports 49160/49161 with a generated
  `OPENCODE_SERVER_PASSWORD`; every serve process was killed afterwards and the
  sandbox deleted. The real opencode store was only ever read via read-only
  GETs on a separate serve. Port 48180 (fleet) was never touched. No real key
  material was printed; all injected keys were obviously-fake throwaways.
- **[spec]** — `GET /doc` OpenAPI 3.1 of the live 1.18.31 serve (dumped and
  summarized; 162 paths).
- **[source:v1.18.31]** — github.com/anomalyco/opencode at tag `v1.18.31`,
  file and line cited.

---

## 1. Credential injection

### 1.1 Where opencode 1.18.31 keeps credentials (four stores)

| Store | Written by | Read by | Notes |
|---|---|---|---|
| `~/.local/share/opencode/auth.json` | `PUT /auth/{providerID}` **[live]**, `opencode auth login` | every CLI/serve process at boot **[source:v1.18.31 auth/index.ts]**; `PUT` honored by a *running* serve without restart **[live]** | the CLI's own API-key store. Absent on this host until written. |
| `opencode.db` `credential` table | desktop/console "Connect" flows (integration `opencode` / `opencode-go` — the two active rows here, labels "Default"/"OpenCode Go") | serve processes via the desktop service bridge **[source:v1.18.31 provider.ts L1620]**; empirically the reason this host works with no auth.json **[live + 001]** | internal schema — never read/write directly (anti-contract, 001) |
| `OPENCODE_API_KEY` env var | process env | provider load, source `env` **[source:v1.18.31 provider.ts L1583-1593]** | models.dev declares the **same var name for both flavours** — one env var connects both with one key; cannot separate zen from go **[live]** |
| `opencode.json[c]` `provider.<id>.options.apiKey` (incl. `{env:VAR}` interpolation) | config file | provider load, source `config` — merged **last** **[source:v1.18.31 provider.ts L1648-1655, L1781]** | per-provider, so it *can* target one flavour; boot-time only **[live]** |

Extra fileless path found in source: `OPENCODE_AUTH_CONTENT` env var — the
entire auth.json content as JSON, taking precedence over the file
**[source:v1.18.31 auth/index.ts]**. Useful if the engine ever wants to inject
without touching disk.

### 1.2 auth.json schema (as accepted and written by the serve API)

`PUT /auth/{providerID}` body is one of **[spec]**:

```jsonc
// ApiAuth — a zen or go API key (what Agent Factory UI keys are)
{ "type": "api", "key": "<key>", "metadata": { /* optional str->str */ } }
// OAuth (console/subscription logins)
{ "type": "oauth", "refresh": "...", "access": "...", "expires": 0,
  "accountId": "?", "enterpriseUrl": "?" }
// WellKnown
{ "type": "wellknown", "key": "...", "token": "..." }
```

Verified live **[live]**: `PUT /auth/opencode-go` with
`{"type":"api","key":"sk-FAKE…"}` → 200, and auth.json appears on disk as
exactly:

```json
{ "opencode-go": { "type": "api", "key": "<redacted>" } }
```

`DELETE /auth/{providerID}` → 200, removes that provider's entry only
**[live]**. There is **no `GET /auth`**; the spec's `GET /provider/auth`
returns auth-*method descriptors* (the login menu: oauth vs "Manually enter
API Key"), not stored-credential state **[live + spec]**. To observe state:
`connected` in `GET /provider`, or `opencode auth list` (which — quirk — reads
**only auth.json** and reports "0 credentials" on this host despite the db
holding both working rows **[live, also seen in 001]**).

### 1.3 Precedence (why "profile key blank = native wins" holds)

Provider assembly order in 1.18.31 **[source:v1.18.31 provider.ts]**: env keys
(L1583) → auth.json `type:"api"` keys (L1596-1606) → plugin-auth loaders via
the desktop bridge (L1609-1627 — the path that surfaces the db credential
rows) → custom → config `options` (L1648). Deep-merge = later source
overwrites earlier; and at client construction `options.apiKey` wins outright,
falling back to the merged `provider.key` (L1781):

> config `options.apiKey` > auth.json key > `OPENCODE_API_KEY` env
> (with desktop-bridge credentials arriving through the plugin-auth layer)

Consequences for the Profiles contract (rule from 025):

- **Profile key BLANK → the engine performs no injection at all.** Whatever
  native credential exists (this host: the db rows via the bridge — verified
  working headless since 001) keeps winning. No code path needs to "prefer"
  native; absence of injection *is* the fallback.
- **Profile key SET → `PUT /auth/{providerID}` with the ApiAuth shape** before
  spawning the fleet serve. auth.json outranks the env var and is read by every
  later process (including the `opencode run` fallback path). Only that one
  provider entry is touched; the other flavour and all non-opencode providers
  are unaffected **[live]**.
- A running serve honors a fresh `PUT` for **inference** immediately
  **[live]**, but the **catalog/palette is computed at boot** (§2) — so the
  engine should inject keys *before* `opencode serve` spawns, and treat key
  rotation as serve-restart.

### 1.4 Recommended injection mechanism (engine contract)

1. On fleet start, for each opencode profile with a **non-blank** key: HTTP
   `PUT /auth/{opencode|opencode-go}` (basic auth with the generated serve
   password) **after** the serve is healthy, **before** any session runs — or
   write auth.json directly before spawn (same JSON); PUT is preferable (it is
   the documented API and already handles the 0600 file).
2. Blank key → no PUT (native wins, per 025).
3. Never use `OPENCODE_API_KEY` (cannot target one flavour), and never write
   `options.apiKey` into opencode.json (it would outrank auth.json and defeat
   the blank-key rule).

---

## 2. Catalog enumeration (per flavour, key present vs absent)

### 2.1 `GET /provider` — shape `{all, default, connected}` **[live + spec]**

- `all`: **array** of every models.dev provider (233 here), each
  `{id, name, env[], models{}, …}` — the whole world, not the two flavours.
  With no key, `all[opencode].models` = the 8 keyless/free models;
  `all[opencode-go].models` = 37 static models.dev entries. These counts do
  **not** grow with a key at runtime.
- `default`: map provider→default model (`opencode: "big-pickle"`,
  `opencode-go: "gpt-5.6-luna"` on 1.18.31 here).
- `connected`: **array of provider-id strings** — not objects, and not a
  credential dump. Observed: `["opencode"]` keyless (zen's free models need no
  key); `["opencode","opencode-go"]` once a go key exists anywhere
  (auth.json, env, or config) **[live]**.

### 2.2 `GET /config/providers` — the palette source **[live]**

`{providers: [{id, name, models{}, …}], default}` — the *effective* provider
set, computed at serve boot from the merge in §1.3. This is what the UI palette
should enumerate, filtered per flavour (`id === "opencode"` → zen palette;
`id === "opencode-go"` → go palette). Observed model counts:

| Credential state | zen (`opencode`) | go (`opencode-go`) |
|---|---|---|
| keyless serve | 8 (the `*-free` list) | **absent** (provider not offered at all) |
| boot with a fake/invalid key in auth.json | 80 (full public zen catalog) | 30 |
| boot with the host's real keys (db via bridge; auth.json absent) | **44** | **30** |

Readings:

- A key (any string) must exist for the go flavour to appear at all; zen is
  always offered (keyless free models).
- Catalog endpoints are not key-validated at list time; with the **real** key
  the zen palette shrinks to what the plan can actually drive (44 ⊂ 80; the 36
  dropped are all paid SKUs). "Exactly the models the key can drive" is
  therefore answered by this endpoint **when a real key is present** — the
  fake-key numbers above are upper bounds only.
- The palette is boot-time: a `PUT /auth` mid-flight does not refresh
  `/config/providers` (observed) even though inference picks the key up.
  Rotate keys → restart the serve.
- go's 30-model list was **byte-identical** fake-key vs real-key **[live]** —
  matches the map's "~30-model open-weight list".

### 2.3 `opencode models [provider]` — CLI, for humans, not the palette **[live]**

With the host's real keys: `opencode` → 44, `opencode-go` → 30,
bare → 74 (only these two providers aggregate). Keyless sandbox: 8 and 1.
Notable: `opencode auth list` says "0 credentials" on this host (reads
auth.json only — §1.2 quirk), yet `opencode models` works — it is not a
credential-state oracle either. **The engine should use
`GET /config/providers` of the very serve process that runs the sessions**, so
palette and execution can never disagree.

> Re-verify when real keys land (tickets 033/034): confirm zen=44 / go=30 via
> `GET /config/providers` on the fleet's own serve, and that the counts
> reproduce after a serve restart.

---

## 3. Per-session model pinning (exact 1.18.31 payloads)

From the live OpenAPI spec **[spec]**, all verified by live calls **[live]**:

- **`POST /session`** body:
  `{ parentID?, title?, agent?, model?: { providerID, id, variant? }, metadata?, permission?, workspaceID? }`
  — note the session-body model field is **`id`**, not `modelID`. Pinned model
  reads back on **`GET /session/{id}`** as `"model": { "id", "providerID" }`
  (variant optional) — the observability hook 024 asks for. Verified end to
  end: create with `{title, model:{id:"glm-5.3-flash-free", providerID:"opencode"}}`
  → readback identical.
- **`POST /session/{id}/message`** (sync) and **`POST /session/{id}/prompt_async`**
  (204, follow via SSE) body:
  `{ parts: [{type:"text", text}], model?: { providerID, modelID }, agent?, messageID?, tools?, system?, variant?, noReply?, format? }`
  — the message-body field **is** `modelID` (asymmetric with session create —
  DTOs must differ). Verified live.
- **No pin** → `GET /session/{id}` returns `model: undefined` (the
  `default` map in `GET /provider` applies at dispatch, not session creation).
  025's "every phase pins explicitly" makes this moot, but the engine should
  not rely on any server-side default.
- The model (message-time or session pin) must exist in the **effective
  catalog**; an unknown id fails the dispatch with HTTP 500
  `ProviderModelNotFoundError` ("Model not found: opencode-go/kimi-k3. Did you
  mean: …") **[live]** — ugly surface; the engine should validate pins against
  its palette client-side.
- There is also `POST /api/session/{id}/model` ("Switch session model") on the
  newer `/api/*` control-plane family **[spec]** — not needed for the engine
  (a phase = one session = one pin at create).

## 4. Both flavours, one serve — verdict: **yes**

Verified on a single serve process **[live]**:

- one sandbox serve drove a **zen** session to completion
  (`opencode/glm-5.3-flash-free` → "pong", cost 0) and, concurrently, a
  **go**-pinned session whose dispatch reached the go upstream API
  (fake key → per-message `APIError` 401 "Invalid credential") — provider
  chosen per session/message, no cross-talk, no server crash;
- key failures surface **per message** (assistant message carries
  `error: {name:"APIError", data:{message, statusCode:401, isRetryable:false}}`;
  serve log logs `stream error` and continues) — the engine detects bad keys by
  watching message error parts, not process health;
- credentials for both flavours coexist in one auth.json (two entries) or one
  native store (this host's db rows). **One serve, both flavours; two serves
  would only be needed if credentials had to be isolated, which 025's model
  never requires.**
- Full E2E (real keys, paid models, both flavours concurrently) re-verifies in
  ticket 034 once 033 provisions the keys.

## 5. Recommended engine contract additions (delta on 001's server-first contract)

1. **Injection**: per flavour, `PUT /auth/{opencode|opencode-go}` with
   `{type:"api", key}` before first dispatch; blank profile key → no call.
   Rotation = PUT + serve restart (palette is boot-time).
2. **Palette source**: `GET /config/providers` of the fleet serve, filtered by
   flavour provider id; cache per serve boot; refresh on serve restart. Never
   `opencode models` (not credential-state-accurate), never `GET /provider`
   `all` (unfiltered models.dev world).
3. **Pinning**: `POST /session {title, model:{providerID, id}}`; per-message
   `{parts, model:{providerID, modelID}}` (note `id` vs `modelID` asymmetry);
   observability reads `GET /session/{id}` → `model.{id, providerID}`.
4. **One serve** runs both flavours; per-message error parts carry auth
   failures (`statusCode` 401) — treat as profile-key errors, not engine faults.
5. **Re-verify with real keys (033/034)**: zen palette count (expect ~44),
   go palette count (30), paid-model E2E on both flavours, key-rotation-restart
   behavior.

## Evidence index

- **[live]** isolated sandbox probes, 2026-10-28: serve on 127.0.0.1:49160
  (`XDG_DATA_HOME`/`XDG_CONFIG_HOME` → temp sandbox, generated
  `OPENCODE_SERVER_PASSWORD`), real-store read-only serve on 127.0.0.1:49161.
  All probes: health, `/provider`, `/config/providers`, `/provider/auth`,
  `PUT`/`DELETE /auth/*`, `POST /session` (+model pin), `POST /session/:id/message`
  (zen free OK; go → upstream 401), `PATCH /session` (title), `GET /doc` dump.
  Fake keys only; sandbox and processes destroyed after; port 48180 untouched
  (fleet listener observed, PID 1768, untouched); Agent Factory app not run.
- **[spec]** `GET /doc`, opencode 1.18.31: `PUT/DELETE /auth/{providerID}`
  (Auth = OAuth | ApiAuth | WellKnownAuth), `POST /session` (SessionData.model
  = {providerID, id, variant?}), `POST /session/{id}/message` (model =
  {providerID, modelID}), `GET /config/providers`, `GET /provider`,
  `GET /provider/auth`, `POST /api/session/{id}/model`.
- **[source:v1.18.31]** anomalyco/opencode:
  `packages/opencode/src/auth/index.ts` (auth.json only; `OPENCODE_AUTH_CONTENT`
  override; 0600),
  `packages/opencode/src/provider/provider.ts` L1583-1593 (env),
  L1596-1606 (auth.json), L1609-1627 (plugin-auth/bridge),
  L1648-1655 + L1781 (config options win),
  `packages/core/src/credential.ts` (db credential table = integration store).
- Prior findings: [001](../../issues/001-research-opencode-api.md) (server-first
  contract, db-credential fallback verified, auth.json absent on host),
  [025](../../issues/025-grill-profiles-destination-and-shape.md) (blank-key
  rule), [024](../../issues/024-map.md) (flavour facts: `opencode/…` PAYG,
  `opencode-go/…` subscription).
