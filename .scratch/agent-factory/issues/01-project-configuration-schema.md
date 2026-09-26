# 01 — Project Configuration Schema

**What to build:** A working YAML config system that reads per-project settings from the `factories/` directory — git repo URL, API keys, LLM provider, and Docker image per project. The agent factory should be able to load a valid config and expose its values to every downstream component.

**Blocked by:** None — can start immediately

**Status:** ready-for-agent

- [ ] YAML config schema defined for a project (git repo URL, API keys, LLM provider, Docker image)
- [ ] Config loader reads a project config from `factories/<project>.yaml` and validates it
- [ ] Config values are accessible to the issue poller, worker, and board components
- [ ] Unit tests for config loading and validation
