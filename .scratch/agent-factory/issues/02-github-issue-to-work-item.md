# 02 — GitHub Issue to Work Item

**What to build:** A single GitHub issue from a configured repo is polled, converted to a work item, and appears on the Kanban board in Backlog. This closes the loop from "GitHub issue exists" → "factory knows about it."

**Blocked by:** 01 — Project Configuration Schema

**Status:** ready-for-agent

- [ ] GitHub issue poller reads issues from the configured repo using project config
- [ ] Each GitHub issue is converted into a work item with title, description, and initial Backlog status
- [ ] Work items are persisted and visible to the board component
- [ ] Unit tests for issue-to-work-item conversion
- [ ] Integration test with GitHub API (mocked or real)
