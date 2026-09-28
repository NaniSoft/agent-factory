# Escalation parks a work item; it does not grave it

When a work item exhausts its three rounds, it escalates. It is never auto-merged on
exhaustion. But escalation is a **parking state, not a terminal one**: a human can still
merge or reject it from the board.

The docs say Escalated is a terminal state, and their state machine labels the edge
"(terminal failure)" rather than "rounds exhausted". We keep escalation as the outcome
because a reviewer who has spent three rounds of attention is exactly the signal that a
human is the right party to finish the work. We reject the terminality because the pull
request already exists, and a work item a human has called wrong three times should not
require a terminal UI excursion — leaving the tool to find a branch and merge it by hand —
in order to be resolved.

This is deliberate asymmetry, and it is the important part: **ignoring a work item merges
it; actively rejecting it protects the repository.** The timeout path is the more dangerous
one. A reviewer who is asleep gets a merge; a reviewer who is paying attention gets a safe
park.

# Consequences

The published docs contain three passages that disagree about this. `introduction.mdx`
says the factory "merges or escalates". `observability-and-error-handling.mdx` says
feedback exhausted closes the loop and escalates. `kanban-and-the-human-feedback-loop.mdx`
cross-references the *auto-merge policy* from the exhaustion sentence, conflating
exhaustion with timeout. `DESIGN.md` is silent, saying only that "the loop closes rather
than continuing". We rule: exhaustion escalates and parks. Nothing auto-merges on
exhaustion, ever.

`DESIGN.md:88` also conflates Rejected and Escalated into one "dead/rejected state", which
contradicts its own Observability section. They are **two distinct terminal states with
different causes** — a human decline versus a failure — and the data model keeps them
separate. This ADR only makes Escalated non-terminal; Rejected stays terminal, because a
human who rejects a change has said something final about it.
