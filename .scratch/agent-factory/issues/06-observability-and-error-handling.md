# 06 — Observability and Error Handling

**What to build:** Structured logging, metrics, retry strategies, and dead letter escalation across the agent factory. Failures in polling, building, or merging are logged, retried, and escalated — visible on the Kanban board where relevant.

**Blocked by:** 01 — Project Configuration Schema, 02 — GitHub Issue to Work Item, 03 — Container Build Worker, 04 — Kanban Board with Feedback Loop

**Status:** ready-for-agent

- [ ] Structured logging across all components (poller, worker, board, merger)
- [ ] Metrics exposed (issues processed, builds succeeded/failed, feedback rounds)
- [ ] Retry strategy for transient failures (API calls, container start)
- [ ] Dead letter escalation for permanently failed tickets
- [ ] Failed/escalated tickets visible on Kanban board
- [ ] Unit tests for retry and dead letter logic
- [ ] Integration test for failure path end-to-end
