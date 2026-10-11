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

Acceptance from Backlog into Frontier was **automatic** when a container slot frees. The
Board is the one place humans act, and it acts through the three review decisions, not by
gating intake.

> **This paragraph is superseded (#40).** Acceptance is a reviewer's button on the board,
> `PromoteAsync`, applied by the loop. It was found to be false of the code: pointing the
> factory at a repository with 810 open issues built every one of them, because the board
> was not in the path of anything. The rule above — no label filter on intake — was kept, and
> only the sentence about what happens afterwards was corrected.

> **And routing is not a label gate either (ADR-0015).** A work item now costs one container
> before it can be built, because a routing round looks at the issue and decides what kind of
> work it is. That is a column the factory writes, derived from the issue, and the factory
> still reads no labels: `ready-for-agent` remains the operator's own pre-filter and the
> reviewer's button remains the gate. A per-project label vocabulary deciding what the factory
> builds is the thing this ADR forbids, and routing from the issue's own words is not it.

The order in which `factories/` projects take their turn is derived from the directory —
sorted by file name — so it is deterministic without anyone maintaining an ordering. The
docs require that it come "from the directory's contents rather than from an ordering
anyone maintains", and sorting is the reading that is both derived and reproducible.
