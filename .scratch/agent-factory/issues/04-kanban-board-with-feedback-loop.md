# 04 — Kanban Board with Feedback Loop

**What to build:** A visual Kanban board at port 5000 with swimlanes (Backlog → Frontier → In Progress → Review → Done). A human can approve, request changes, or reject a ticket. Auto-merge triggers on approval; max 3 feedback rounds per ticket before forced auto-merge.

**Blocked by:** 02 — GitHub Issue to Work Item, 03 — Container Build Worker

**Status:** ready-for-agent

- [ ] Kanban board UI renders swimlanes: Backlog → Frontier → In Progress → Review → Done
- [ ] Board auto-refreshes and shows real-time status updates at port 5000
- [ ] Human can approve a ticket → triggers auto-merge
- [ ] Human can request changes → ticket returns to worker for another build (max 3 rounds)
- [ ] Human can reject a ticket → ticket moves to a dead/rejected state
- [ ] Feedback timeout triggers auto-merge after threshold
- [ ] Unit tests for feedback state machine
- [ ] E2E test for approve → merge flow
