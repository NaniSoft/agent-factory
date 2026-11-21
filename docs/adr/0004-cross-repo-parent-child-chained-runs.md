# Cross-repo tickets run as parent/child runs on chained branches — originals close as duplicates

A cross-repo ticket (work spanning several repos of an org) is executed as a
**parent run** on the routed `.github` issue whose one planning session emits
ordered **child specs**, followed by ordinary single-repo **child runs** — not
one session driving multiple worktrees, and not independent children on main.
Alternatives rejected for specific reasons:

- **One session, multiple worktrees** reuses none of the run machinery and
  breaks the one-worktree blast-radius model (ADR 0002's posture).
- **Children independently based on main** cannot work: in the motivating
  scenario (rename an API in A, adapt consumers B and C), B and C must compile
  against A's *unmerged* API. Children therefore run **sequentially with
  chained bases** — A on main, B on A's branch, C on B's — producing **stacked
  PRs** that contain ancestors' commits until the earlier ones merge. That
  stacking is expected, not a defect; humans merge in order (ADR 0001
  unchanged).

Routing is deliberately irreversible-looking: the original issue is commented
and **closed as a duplicate** of the new `.github` issue (autolinked), because
an original left open can be picked up again — guaranteed double work. The
org's `.github` repo is created lazily and private at first routing, since
visibility only matters for health files, not issues.

Failure semantics: children retry individually (v1 contract per run); a
terminal child failure **cancels downstream children** (their bases are dead)
while green children stand, and the parent ends failed-or-partial with a
summary on the `.github` issue. Re-triggering a parent is always a **full
fresh pass** — no surgical re-runs of a subset (decided in ticket 014,
2026-10-24).
