# A round is a whole agent session, not one agent invocation

A **round** is one complete attempt at a work item: the agent runs in a single fresh
worker container, builds, tests, corrects its own mistakes, and returns a result the
reviewer can judge. The three-round ceiling is three such attempts per work item.

We chose this over the tighter reading, where a round would be a single invocation of
the agent inside the container and self-correction would have to come from the
reviewer instead.

The reason is that the tighter reading spends the reviewer's attention on work the
agent should have done itself. A round ceiling is a brake on *disagreement* between
the agent and the reviewer; if every round is a single invocation with no room to
react to a failing test, then most rounds would come back failing on something the
agent could have caught and fixed, and the ceiling would be measuring the agent's
inability to iterate rather than a genuine disagreement. Three rounds of real work
is a meaningful budget; three rounds of one shot each is not.

# Consequences

The container lifetime and the round boundary are the same thing. One container is
started per round and destroyed when the round returns, which is what makes the
"nothing survives a round" guarantee fall out of the design rather than needing to be
enforced separately.

The cost is that a stuck round is expensive — a single round has no bound other than
the timeouts, so the round timeout is load-bearing and has to be set deliberately
rather than inherited from whatever default the agent framework happens to use.

The 3-round ceiling and the feedback-timeout auto-merge are measured in rounds and
in wall-clock time respectively, so they are not interchangeable: a reviewer who
requests changes quickly gets a third round, while a reviewer who walks away triggers
the merge instead. Both paths must be measured separately.
