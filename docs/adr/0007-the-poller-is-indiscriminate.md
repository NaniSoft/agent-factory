# The poller is deliberately indiscriminate

Every open issue on a configured repository becomes a candidate work item. There is no
label filter, no assignee filter, and no gate in the schema. Work items land in Backlog,
and the board is where a human decides what is worth building.

The docs are firm on this — "every open issue on a configured repository is a candidate
work order" — and the schema deliberately carries no such field, on the stated grounds
that such a field "would move factory policy into project configuration, and the factory's
promise that a project is a file depends on that line holding."

# Consequences

This collides, on its face, with this repository's own `AGENTS.md`, which defines a
five-label triage vocabulary including `ready-for-agent`. A future reader will notice that
the factory ignores exactly the label the repository tells its agents to apply, and will
reasonably assume a bug. It is not a bug. A label gate would make the factory's behaviour
depend on per-project conventions, which differ per project — the precise thing the design
forbids. `ready-for-agent` is the operator's own pre-filter, applied before the issue ever
reaches the board. Triage happens in Backlog, on the board, which is the only surface the
design trusts for human input.

Acceptance from Backlog into Frontier is **automatic** when a container slot frees. The
Board is the one place humans act, and it acts through the three review decisions, not by
gating intake.

The order in which `factories/` projects take their turn is derived from the directory —
sorted by file name — so it is deterministic without anyone maintaining an ordering. The
docs require that it come "from the directory's contents rather than from an ordering
anyone maintains", and sorting is the reading that is both derived and reproducible.
