# The factory is one process, not a set of services

The orchestrator, the Kanban service, the issue poller, the config loader, and the
merger are one .NET process. The board is an ASP.NET Core endpoint on port 5000 served
by that same process, and the components talk to each other as ordinary method calls.

The published Nexus docs assert this ("all sit in the same runtime, which is what makes
their contracts ordinary method calls rather than network guesses"), but `DESIGN.md`'s
deployment section reads as a multi-service topology, and its component table names a
separate "Kanban service" that "publishes the board at port 5000". We follow the docs
and correct `DESIGN.md`.

# Consequences

`DESIGN.md:23` is also wrong in a way that follows from this: the Kanban service holds
**no** board state. The docs are explicit that there is no board-side copy to drift out
of sync. The board reads the same records the orchestrator writes, and in one process
that is not a consistency property we have to maintain — it is the absence of a second
copy.

The Compose file becomes trivial: one application container, plus the Docker runtime it
starts worker containers against. If concurrency or deployment pressure later demands
separate processes, the method-call contracts are the seam that makes extraction
possible, which is the same argument the docs make for the one-runtime choice.
