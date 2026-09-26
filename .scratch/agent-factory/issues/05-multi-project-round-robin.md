# 05 — Multi-Project Round-Robin

**What to build:** Multiple projects configured in `factories/`, each processed in round-robin order with fresh containers per issue. The factory distributes work evenly across projects and does not block on a single project's pipeline.

**Blocked by:** 01 — Project Configuration Schema, 02 — GitHub Issue to Work Item, 03 — Container Build Worker, 04 — Kanban Board with Feedback Loop

**Status:** ready-for-agent

- [ ] Factory loads multiple project configs from `factories/` directory
- [ ] Issues are polled from all projects in round-robin order
- [ ] Workers process issues from different projects without blocking each other
- [ ] Each issue still gets a fresh container environment regardless of project
- [ ] Board shows tickets grouped or filterable by project
- [ ] E2E test with two or more configured projects
