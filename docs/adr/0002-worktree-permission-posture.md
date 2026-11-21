# Fleet sessions run with edit+bash allowed, scoped by worktree — not sandboxed

Unattended sessions cannot answer interactive permission prompts, so fleet runs
ship a per-worktree `opencode.json` granting `edit: allow` and `bash: allow`
(the documented per-project lever), rejecting `--auto` as less visible and less
configurable. This is **not a sandbox**: bash runs as the user, and the scoping
only bounds the *repo* blast radius (one worktree of the playground repo).
Recorded so nobody later mistakes the worktree boundary for machine-level
containment (decided in ticket 004, 2026-10-24).
