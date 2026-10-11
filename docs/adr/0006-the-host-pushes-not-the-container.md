# The host pushes; the container never holds a write credential

A round's changes leave the container as a **commit**, not as a pushed branch. The worker
container commits locally and holds no credential that can write to a remote. The host
retrieves the commit, pushes the branch, and opens the pull request under the factory's
own token.

The design says the merger "opens and merges the pull request" and that "the build agents
inside a worker container receive the credentials their project declares" — but nothing
anywhere says how changed files get from the container to the merger. That is a real hole,
and it decides the security posture, because it decides which credential lives inside the
untrusted box. We closed it by keeping every write credential on the host.

# Consequences

The agent never holds anything that can change the repository it was told to read. A
compromised, confused, or prompt-injected worker cannot push anywhere at all — not to the
target branch, not to a new branch, not to a fork. This is materially stronger than the
alternative, where the agent's own token would let it write to the remote and the factory
would depend on the agent not misbehaving.

It also makes the published claim literally true rather than aspirational: the component
that writes the code is not the component that ships it. The Nexus docs put it well — "the
build agents cannot decide whether their own work ships" — and this is the mechanism that
guarantees it.

The retrieval is unglamorous and reliable: the container commits to a branch, the host
pulls the commit out of the container, and pushes from the host. That mechanism is still
to be specified; see the spec.

This is why the factory must not be given a token that can write to its own repository
until its merge path is trustworthy — see `docs/agents/issue-tracker.md`.

## Amended: the host creates issues too (ADR-0015)

The host now also writes to the issue tracker, which this ADR does not change and does
weaken in one place worth naming.

A round proposes tickets — a map, its decision tickets, its questions — by writing a
structured file, and the host creates them once the container is gone. The alternative was
to hand the round an issues-scoped credential so the agent could file its own tickets live,
and it was rejected for the reason this ADR gives rather than despite it: a box holding an
agent and an LLM key does not become safer by also holding a token that can write, and the
component that plans the work would then also be the component that publishes it.

So the boundary is unchanged in kind and widened in scope. Every write credential is still
on the host; the host now uses the project's `keys.github` for four more things than it did.
The container still cannot push, cannot comment and cannot open an issue.
