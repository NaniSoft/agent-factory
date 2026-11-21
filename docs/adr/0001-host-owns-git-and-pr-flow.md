# Host owns the git and PR flow; the agent never leaves its worktree

Fleet agents implement, test, and commit inside a per-run worktree, but the C#
host alone pushes branches, opens PRs, and comments on issues. This keeps the
`gh` credentials (full repo scope) out of the agent's bash scope — the
permission posture grants bash freely inside the worktree, so letting the agent
push would hand it the token — and gives the engine one place to own retry and
failure semantics (decided in ticket 004, 2026-10-24).

## Considered Options

- **Agent-owned full git flow** (agent pushes and opens the PR itself):
  rejected — simpler engine, but it puts authenticated GitHub access inside a
  bash-allow session.
