# No Microsoft Agent Framework in v1

The orchestrator in v1 is a hand-rolled deterministic state machine. It does not use the
Microsoft Agent Framework, and it hosts no agents of its own.

`DESIGN.md:26` says the orchestrator "is built on the Microsoft Agent Framework, which
sequences the agents involved in a build." The published docs name four activities — plan,
build, check, report — but never enumerate the agents, never give a count, and never
resolve whether they run host-side or inside the container. Meanwhile OpenCode, in the
container, already plans, builds, and checks. So MAF's described role overlaps with the
tool that does the actual work, and the documentation cannot name what it would add.

The MAF packages are real and capable — `Microsoft.Agents.AI` is stable at 1.17.0, and its
graph workflows do ship checkpointing and human-in-the-loop, which genuinely resembles
this state machine's approval gates. We are declining that on cost, not on merit.

# Consequences

Polling issues, starting a container, driving one agent, persisting a result, waiting for
a decision, and merging is a state machine, and the design specifies its states and
transitions exactly. A hand-rolled machine for it is a couple of hundred lines and is
fully testable with no framework at all. Every MAF abstraction we adopted would be a
concept the codebase then has to speak, defined against agents we cannot yet name.

The round ceiling, the auto-merge threshold, escalation, and retry all live in this
machine, which the docs already identify as code that "can be exercised without a
container". Keeping it framework-free is what makes that true cheaply.

This is a deliberate divergence from `DESIGN.md:26`. If MAF turns out to earn its place
later, retrofitting it is real work — which is the reason to revisit it deliberately
rather than by default.
