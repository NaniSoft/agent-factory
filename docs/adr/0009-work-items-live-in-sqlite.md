# Work items are persisted in SQLite

Work items, their rounds, and their results live in a SQLite database file. It is the
factory's store of record.

The docs assert four times that work items are persisted and that the board reads the same
records the orchestrator writes; they never name a medium. With the factory in one process
(ADR-0003), the hard part of persistence — two writers seeing the same state — disappears,
which makes a real store nearly free.

We rejected an in-memory store because losing a work item on restart means losing an
agent's entire round of work, and the design's promise is that a work item is a durable
thing. We rejected JSON files because querying work items by state, for both the board and
the round-robin, becomes real work in raw JSON and concurrency becomes a rewrite. We
rejected Postgres because it adds a container to run, secure, and back up, in exchange for
concurrency and query power this design does not yet need.

# Consequences

SQLite is a single-writer store, which matches one process exactly. If a second writer
ever appears — a second factory instance, or a genuinely concurrent writer inside this one
— this is the decision to revisit, and the migration is real work.

Round results — the files changed, commands run, and test outcomes — travel with the work
item. They can get large, so they are stored as a structured payload rather than shredded
across columns, and the board reads them whole to render a review.
