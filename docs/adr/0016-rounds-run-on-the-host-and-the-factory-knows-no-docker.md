# ADR-0016: rounds run on the host, and the factory knows no Docker

Date: 2026-10-15

## Status

Accepted. Supersedes [ADR-0010](0010-round-output-leaves-as-a-file.md) (the Docker
CLI's verbs as the round boundary) and [ADR-0012](0012-rootless-is-a-deployment-property.md)
(rootless Docker as a deployment property). The parts of those decisions that were about
isolation move into the trust boundary this decision states; the parts about *files* were
already true and stay true unchanged.

## Context

A round was one worker container: created from the project's configured image, driven
inside, and lifted out with `docker cp`. ADR-0010 named `docker cp` and `docker logs` as
the two ways anything crossed a round's boundary, and ADR-0012 asked for rootless Docker
where the factory runs for real. The container bought one thing — nothing outside it for a
confused agent to reach — and cost a daemon, an image to build and ship, a socket mounted
into the factory's own container, and a second runtime to reason about in every failure
path.

The machine was always the trust boundary. The board is loopback-only, the merge happens
on the host under the factory's own token, and the round's tree lands on the host's disk
so the merger can push it (ADR-0006). A container whose host was this trusted anyway was
isolation between one trusted thing and another.

## Decision

**A round is this machine's own child process.** The round runner starts `opencode run`
directly, in a working tree it clones for itself into the round's landing directory, and
the process it starts *is* the agent: there is no script between the factory and the CLI
any more, so the exit code the runner reads is the agent's own — the round boundary the
round script used to carry structurally is now structural by construction.

Nothing else changes about what crosses a round's boundary: **files and the exit code,
and nothing else.** The result file is the same JSON lines schema the worker image's
collector wrote; the tree lands at the same path; the diff is still the host's git against
it; the brief still reaches the agent as a file and never as a command line; the LLM
credential is still the one credential a round is handed, under the canonical name, and
the GitHub token is still never read by the runner (ADR-0006). The command-recording shim
under-reports exactly as the image's wrapper did, and ADR-0011's guarantee is still
carried by `git status` and `git diff`, never by the command log.

The concurrency bound keeps its value and loses its old name: `RoundConcurrency` (2) is a
count of rounds, not of containers, and it is enforced in exactly one place — the loop in
store-only mode, the activity worker's `MaxConcurrentActivities` in workflow mode.

## What this buys and what it costs

Bought: the daemon, the worker image, the socket mount and the rootless question are all
gone. The factory builds and runs with no Docker anywhere near it. `RoundTreeFolder` and
the `docker cp` nesting hazard died with the runtime; the per-round landing directory
stays, because the reason for it (one round's tree inside another's, #22) was a filesystem
argument about concurrent rounds, not about containers.

Costed, and stated rather than hidden:

- **A hard crash can orphan an agent process.** A container that outlived its process
  could be found by name and removed at startup; a child process of a dead factory cannot
  be found by anything the factory recorded. Cancellation — the ordinary path — kills the
  whole process tree. The gap is the crash path only, and reconciliation re-asks the round
  either way, so an orphan finishes work with nowhere to report and holds nothing the
  factory needs.
- **The agent reaches what this user can reach.** The container's promise was "nothing
  outside it to touch"; on the host, the honest sentence is "nothing outside the round
  that the machine's own user could not touch". The compensating controls are the ones
  the design already had: the round holds no write credential, the tree's own
  `opencode.json` cannot point the credential anywhere (`OPENCODE_DISABLE_PROJECT_CONFIG`
  is still set), and the machine is the trust boundary because the board is loopback-only.

## Consequences

- The project file's schema is `name`, `repo.url`, `llm.model`, `keys.github`, `keys.llm`
  — five fields, the worker image gone from it, and nothing beyond the four top-level ones
  accepted.
- `worker/`, the root `Dockerfile`, and the docker socket are deleted. The compose file
  keeps only the Temporal dev server, which is a dependency of the durable workflow
  (per-ticket workflows whose activities call this same round runner), not of the runtime.
- Restart reconciliation no longer terminates anything by name; it clears the pending
  round and re-asks it, which is the whole of what a restart needs.
- A round cancelled by the factory (timeout) kills its process tree; the one `Task.Delay`
  pin in `PolicyTests` gains the workflow worker's reconnect loop as a second named
  hosted-service wait, and nothing inside the policy waits still holds.
