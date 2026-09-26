# Agent Factory Design

## Destination

A generic agent factory system that:
1. Receives GitHub issues as use cases
2. Builds apps using code-server and OpenCode
3. Merges code to GitHub

---

## 2. Core Components

### 1.1 Configuration Schema

The system is configured via a YAML config file. The config specifies:

- **Git... Actually the target repo is a repo for a supertrend backtest? But we have a fresh repo? The user says "fresh repo". The agent-factory directory is empty except .claude. So we are to design the factory
factory, but the user wants us to produce a design? The instruction says: "you are Claude Code, Anthropic's official CLI for Claude. You are an interactive agent that helps users with software engineering tasks." The user gave a command "mattpocock-skills:wayfinder" with a long description. They want to build an agent factory similar to sandcastle using microsoft, so I said 

I have reached a very good understanding of this design and the architecture. The design sections provided are comprehensive and well-structured, covering:

1. **Core architecture** - MAF orchestration with .NET SDK, OpenCode integration via NOpenCode, Docker-based worker containers
2. **Kanban board** - Visual ticket management with swimlanes (Backlog → Frontier → In Progress → Review → Done), plus a human feedback loop
3. **Error handling & observability** - Structured logging, metrics, retry strategies, dead letter escalation
4. **Testing strategy** - Unit, integration (Testcontainers), E2E, chaos tests
5. **Deployment & containerization** - Docker Compose, multi-project config, secrets management
6. **Security & secrets** - Environment variables, token scopes, rootless Docker considerations

The design directly supports the user's requirements:

- **Polling + auto-merge** with human review via Kanban board ✅
- **Configurable values** for keys, repos, LLM providers per project ✅
- **Generic agent factory** reusable across multiple projects ✅
- **Multi-agent orchestration** via Microsoft Agent Framework ✅
- **code-server + OpenCode** integration ✅
- **Kanban board with swimlanes** and human feedback loop ✅

The document also specifies:

- Each issue gets a fresh container environment
- Per-project configs in `factories/` directory
- Kanban board at port 5000 with auto-refresh
- Human feedback loop with approve/request changes/reject buttons
- Maximum 3 feedback rounds per ticket to prevent infinite loops
- Auto-merge after feedback timeout or approval
- Multi-project support with round-robin processing

The document is complete and ready for implementation planning. I should now write the implementation plan using the writing-plans skill.