# Every issue is routed before it is built, and the factory writes its own children

A work item does not go straight from Backlog to a build. It goes through a **routing
round** first, which is `ask-matt`'s question asked of an agent: can this be implemented
directly, does it need to be thought through with a human first, or is it too big and too
foggy to plan in one session?

## Why

The factory was pointed at a repository with 810 open issues and built every one of them,
because a ticket that says "fix the thing" and a ticket that says "we have no idea how to do
this" are the same thing to it. Both are open issues; both become work items; both get a
container. The first is a two-hour change and the third is a quarter of a year's work, and
the factory cannot tell them apart until it has spent a container discovering that.

The human gate (#40) fixed the *cost* half of that — nothing is built until a reviewer says
so — but not the *shape* half. A reviewer reading Backlog is asked "is this worth building?"
about a ticket whose answer is almost always yes, because the maintainer filed it. What they
cannot see from the board is which tickets are one round away from done and which ones are
not a ticket yet.

Routing moves that judgement to the agent and leaves the human gate where it was. It is not
a replacement for the gate: a routed `Build` item is still in Backlog and still waits for a
reviewer to accept it.

## The three verdicts

| Verdict | Means | Goes to |
| --- | --- | --- |
| `Build` | the issue is already a build ticket | Backlog, waiting for a reviewer's click |
| `Grill` | implementable, but every branch of the design is not decided | a human is asked, in the issue tracker |
| `Wayfinder` | the way to the destination is not visible | the factory charts a map of decision tickets |

`Grill` and `Wayfinder` both park the work item where a human can finish it. They differ in
what the human is asked for — an answer, or a map — and the distinction is worth keeping
because the two shapes have nothing downstream in common.

## The routing round does not clone the repository

This is the load-bearing constraint, and it is a cost decision rather than an architectural
one. A routing round reads the issue's **title and body** and nothing else: no clone, no
toolchain, no build.

The routing question is about the *shape of the request* — is this a button, or is this a
migration — and that is answerable from what a maintainer wrote. What is **not** answerable
from the title and body is whether the repository already implements the thing, which is
`/triage`'s redundancy check rather than `/ask-matt`'s routing decision, and it needs a clone
to answer honestly. So the factory does not answer it and does not claim to. An issue that
is already built gets routed `Build` and the round discovers that by reading the repository,
which is the correct place to discover it.

The cost of that choice is one container per open issue, spent automatically, before any
human has looked at the issue. That is a real cost and it is bounded by the same
`ContainerBudget` as everything else: routing rounds compete for slots with build rounds and
are not prioritised over them, so a repository with 810 open issues spends its two slots
routing for a long time and builds nothing until the reviewer says which of the routed
`Build` items to accept. That trade is the point — the reviewer's queue becomes a queue of
*tickets that can be built*, rather than a queue of requests.

## The factory writes issues, and the container still holds nothing

A routing round's verdict is a *proposal*. Where it lands is the host's decision, because
the host is where every write credential lives (ADR-0006).

The round writes a structured file, `/out/routing.json`, and the host reads it after the
container is gone — the same shape as `result.json`, read by a deriver that cannot ask the
agent anything (ADR-0011). The host then creates the issues: the map, the decision tickets,
the question sets. It links each child to its parent as a GitHub sub-issue and wires the
blocking edges with GitHub's native dependency relationship, so the frontier renders in
GitHub's own UI rather than only in the factory's.

Passing an issues-scoped credential into the round would have been simpler and was rejected
for the reason ADR-0006 gives: a box holding an agent and an LLM key does not become safer by
also handing it a token that can write. The container proposes; the host disposes.

## Readiness is a field, not a label

The work item's kind is a **column the factory writes**. The factory still never reads an
issue's labels (ADR-0007), and `ready-for-agent` stays the operator's own pre-filter applied
before the issue reaches the board.

This is deliberate and it is the boring choice. Reading the label would dogfood the skills'
model end to end, and it would also make the factory's behaviour depend on a per-project
vocabulary — the exact thing ADR-0007 forbids. The factory learns what kind of work an issue
is by looking at the issue and deciding, which is a thing it can be right and wrong about
visibly, rather than a thing it inherits from a convention.

Labels are still applied to the issues the factory creates. They are for humans reading
GitHub and for the tools around it; they are not an input.

## A verdict nobody can read is a park, never a guess

`/out/routing.json` that is missing, truncated, or carries a verdict the factory does not
know is not a default. The work item is parked with the reason on the card.

An unreadable result reports no files and no commands rather than reporting none, because
"this could not be read" and "this round changed nothing and ran nothing" are very different
claims. The same is true of a verdict: a work item routed to nothing is parked saying why,
and a work item that was never routed is still `Unrouted`, which the board renders as its
own state rather than as a `Build` that has not got round to saying so.

## Consequences

- Every open issue costs one container before anything is built. The budget bounds it; the
  reviewer's gate does not wait for it.
- The factory writes to the issue tracker, not only to pull requests. That is four new calls
  on the one `HttpClient` in the process, all of them under the project's `keys.github`,
  all of them after the container that proposed them is gone.
- A work item is no longer the same kind of thing throughout its life. It is an issue, then
  it is a routing verdict, then it is either a build ticket or a map. That is a `Kind` column
  and a lane, not a change of what a work item is.
- The routing round is the only round that does not fetch the repository, which makes the
  round script a per-purpose thing rather than one fixed script. `roundExitCode` is still the
  agent's exit code in both, because both scripts end with the `opencode run` and nothing
  after it.