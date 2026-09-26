# 03 — Container Build Worker

**What to build:** A fresh Docker container spins up for a work item, runs code-server and OpenCode, builds the code from the issue, and returns the result. Each issue gets its own isolated container environment.

**Blocked by:** 01 — Project Configuration Schema, 02 — GitHub Issue to Work Item

**Status:** ready-for-agent

- [ ] Worker accepts a work item and starts a fresh Docker container
- [ ] Container runs code-server and OpenCode
- [ ] Code from the GitHub issue is built/tested inside the container
- [ ] Build results are reported back and persisted
- [ ] Unit tests for worker orchestration (mock container)
- [ ] Integration test with Testcontainers
