# The Board app renders the factory's judgement and decides nothing

The new Board app (`admin/`) is a thin renderer. It fetches the factory's own view
models over `/api/*` and renders them; it holds no policy of its own. Which lane a
decision means, whether silence can merge, how much of the container budget is in
use, what a diff left off and which intake state a project is in are all the
factory's judgements, made by the components that already make them
(`Orchestrator`, `Pages/HowToReadIntake`, `Pages/HowToReadTheDiff`,
`Pages/HowItEnded`, the store). The API serialises those judgements; the app
formats them for a reader and re-decides none of them.

The alternative, and the reason this is a decision rather than an implementation
detail, is a client that computes any of it. A board that derived its own swimlanes
would be a second state machine free to disagree with the loop about the same work
item; a board that worked out for itself whether a merge landed would be a second
answer to what "Done" means. The factory has one answer to each of those questions
and the app is not allowed to grow a second.

# Consequences

`GET /api/board` returns the budget as `Orchestrator.RoundsInFlight` against
`FactoryConstants.ContainerBudget`, exactly as the Razor board's header reads it,
and the mode as `FactoryOptions.AutoMerge`. Later endpoints reuse the same
judgement classes the pages do, because the API lives in the same assembly and can
call the `internal` ones directly; the layer that is added is a serialiser and a
DTO, not a second set of rules.

There is deliberately no codegen between C# and TypeScript. The endpoint's shape is
pinned by its own records and the app's by a fixture that mirrors a real response,
held together by tests, so a difference is a failing test rather than a generated
file two tools have to keep producing.

The app writes nothing it should not: the only writes it will grow are the ones the
factory's existing page handlers already have, each mirroring a handler and
inventing no capability. A surface that could change a work item another way would
be reachable and therefore subject to the same "the board is the only way a human
can change anything" check the Razor board is.
