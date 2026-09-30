# Live Deployment Spec

The spec for the factory as a deployment a single reviewer runs on their own
machine: repos configured through the factory's own UI, issues built end to end
by OpenCode, the change verified in a review workspace, and the pull request
opened and merged by a human's decision. Every clause here was decided on the
[wayfinder map](https://github.com/NaniSoft/agent-factory/issues/27) and its
tickets; this document is the whole of it in one place, and the implementation
is built against it. [`DESIGN.md`](DESIGN.md) remains the engine's design and is
not restated here — where the two touch, this spec extends it and names the
ticket that decided.

## The destination

`docker compose up` stands the factory up. A repository is added through the
board. An open issue on that repository is polled, built by OpenCode in a fresh
worker container, and appears on the kanban board with its diff. The reviewer
opens a review workspace, verifies the change with the project's own toolchain,
and decides. An approval opens and merges the pull request. Nothing merges
unattended, because auto-merge is off until it is turned on.

## Exposure (ticket #30)

The machine is the trust boundary, and the deployment says so twice.

- **The board** is mapped `127.0.0.1:5000` host-side; Kestrel binds `0.0.0.0`
  inside the container so the mapping works. There is no login. The limit —
  *only this machine can decide what merges* — is stated in the compose file's
  comments and on the page footer.
- **Review workspaces** take the first free host port from a constant range
  (`FactoryConstants`, `7100–7199`), always published with host IP
  `127.0.0.1`. Exhaustion of the range is a rendered state on the card, never
  silence.
- **code-server runs `--auth none`.** The loopback binding is the one gate; a
  password displayed next to its link on the same machine would protect
  nothing. The posture is stated in the workspace's board section.

LAN access later is a deliberate flip of two named things — the mapping and an
auth check — never an accident.

## The review workspace (tickets #31, #35)

A review workspace is a **second kind of container**, not a round: the
project's image with the round entrypoint overridden — no result file, no
recording wrapper, no `worker-round`. `CONTEXT.md` carries the term.

1. **Existence** — on demand, at most one per work item, from an *Open
   workspace* button on a card in Review. The factory spawns on click and
   renders the link.
2. **Contents** — a *copy* of the host's lifted tree for the latest round,
   placed at `/work` by `docker cp`. The host tree is the merger's push source
   and is never shared with the workspace.
3. **No credential** — nothing is placed inside, so there is no push path, and
   nothing done inside affects the shipped change. The reviewer browses and
   runs builds and tests; that is the whole point and the whole permission.
4. **Ends** — on any decision, on a new round starting (its tree went stale the
   instant the round began), or at a fixed lifetime from spawn
   (`FactoryConstants`, 4 hours) with time remaining rendered on the card.
   Re-spawn is one click and always reflects the current host tree.
5. **Failure** — spawn failure, a missing image, and port exhaustion render on
   the card; the diff view remains the fallback. Nothing in the failure path
   ends in silence.

## The model reference (tickets #38, #39)

- **`llm.model` replaces `llm.provider`** — the project file states the full
  reference (`anthropic/claude-sonnet-4-5`), validated to contain a `/`, and it
  reaches the OpenCode CLI as `-m`, verbatim. The schema stays six values.
- **The factory re-emits the canonical credential name.** `EnvironmentFor`
  derives the canonical env var from the provider prefix (`anthropic/…` →
  `ANTHROPIC_API_KEY`) and injects the project's named credential under that
  name. An unknown provider prefix is a load-time refusal, rendered on the
  board — never a silent `Model unavailable`.
- **`OPENCODE_DISABLE_PROJECT_CONFIG=1` on every round.** The factory assumes
  its workloads are untrusted; a tree-controlled `opencode.json` can point the
  provider endpoint anywhere, which makes the round's key an exfiltration
  channel. Trees do not configure the agent.

## Secrets (ticket #32)

- **One file per credential; the name is the file.** `secrets/<NAME>` holds the
  raw value, trimmed. No index, no syntax.
- **Resolution is composite and lazy**: process environment first (today's
  behavior untouched), secrets directory second, read at use time. Rotation
  needs no restart; a running round keeps the value its env carried.
  `FactoryOptions.SecretsDirectory` names the directory (default `secrets`).
- **Injection is unchanged**: the LLM key into the worker under the canonical
  name; the GitHub token host-only — merger header and the one `git push`
  child. The composite reader lives behind `ICredentialReader`, so the
  credential boundary `PolicyTests` IL-scans keeps its shape.
- **The UI form is write-only**: posting writes the file (best-effort
  `chmod 600`, stated as such); the page shows name and set/unset, never a
  value; removal deletes the file.

## Auto-merge (tickets #36)

- **`FactoryOptions.AutoMerge` — default off.** Off, the feedback-timeout path
  never fires: a work item waits in Review until a human decides. On, the
  designed behavior in [`DESIGN.md`](DESIGN.md) applies as written.
- **Factory-level only**, never per-project — factory behaviour is the
  factory's.
- **The board renders which mode is live**, visibly: a reviewer must never
  wonder whether the absence of their decision can merge.

## Project configuration UI (tickets #28, #33)

- The board gains a projects surface: list with load state, add, edit, remove.
  The form fills exactly the six project values (`name`, `repo.url`,
  `worker.image`, `llm.model`, `keys.github`, `keys.llm`) and writes
  `factories/<project>.yaml` — the file stays the single source of truth.
- **Validation failures render where the human acts on them**, from the same
  `ProjectLoadReport` the board already shows; the UI invents no validation of
  its own beyond required-field checks.
- **The loader reads once at startup** (loader research, ticket #28). The
  factory gains a reload action on the projects page that rebuilds the
  `ProjectLoadReport` registration — the honest seam the snapshot architecture
  lacked — and the page says plainly that a change is live after reload.
  Restart remains the fallback and the page says that too. A live-reload seam is honest fog on the map rather than a page that pretends.
- **The credentials form is write-only** per the secrets contract. The board
  remains the only surface where work is approved; projects are configured
  there too, but decisions are never made anywhere else.

## Deployment packaging (ticket #34)

- **One application image**: the .NET process published into a container.
- **docker-compose.yml** carries: the Docker socket mounted so worker and
  workspace containers land as siblings on the host daemon (the ADR-0012
  rootful deviation, recorded in the compose comments); volumes for
  `factories/`, `secrets/`, and the SQLite store; the `127.0.0.1:5000` mapping;
  no pre-declared workspace ports (the factory publishes them itself).
- **The worker image is a prerequisite**: compose says which image it expects
  and the factory says loudly when it is absent, rather than failing a round
  mid-flight.
- `docker compose up` requires no hand steps beyond having Docker Desktop
  running and the worker image built.

## Out of scope, as decided

Multi-user auth and productization; a rootless daemon on this deployment;
code-server inside the round container; per-project auto-merge; reconciling the
frozen Nexus public docs. The live end-to-end proof (ticket #37) is the human's
step — it needs their machine, real credentials, and a real review decision.
