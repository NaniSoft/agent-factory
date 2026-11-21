# Intake is a per-target rule — the label gate stays the default; `all open issues` is an explicit opt-out

v1 made the `agent-factory` label the only way an issue became fleet work
(humans label to opt in; ticket 004). v2 enrolls repos and orgs at will, and a
single global rule no longer fits: the destination wants "a repo's issues are
scanned and worked on add", but silently working a 500-issue backlog on
enrollment is a foot-gun. Intake is therefore a **per-target setting**:

- **`label`** (default) — the v1 contract, unchanged: only labelled issues run,
  and re-adding the label re-triggers.
- **`all open issues`** — chosen explicitly at enrollment: the issue gate is
  dropped for that target. The existing backlog is worked **oldest-first,
  capped at enrollment** (~20) so a backlog cannot starve the rest of the
  fleet; the remainder is picked up as capacity allows.

Reverses the v1 label-only gate **for targets configured `all` only** — the
safety default survives everywhere else. Recorded because "issues become fleet
work without a human touching them" is surprising without this context, and
because an over-eager `all` enrollment consumes real agent budget before
anyone notices (decided in ticket 013, 2026-10-24).
