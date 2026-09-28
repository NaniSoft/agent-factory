# The factory derives the result; the agent only narrates

The result payload — files changed, commands run, test outcomes — is produced by the
factory observing the container, not by the agent reporting. The agent contributes one
optional short prose note, and the round is fully recorded without it.

Files changed come from `git status` and `git diff`. Commands run come from a shell wrapper
recording each invocation. Test outcomes come from the exit code of whatever the agent ran.
The design names these three fields and gives them no schema, and separately admits the
payload is fragile: "a results payload that parses but is missing the field the reviewer
needs" is listed as a failure only integration tests catch.

We rejected having the agent author the result file against a JSON contract, because a model
that produces malformed, truncated, or incomplete JSON takes the round's entire work with
it — the reviewer then sees nothing at all for a build that may have been fine. Observational
data has no equivalent failure mode: `git diff` is `git diff`.

# Consequences

This settles the design's own internal contradiction about test commands. The docs say "the
factory does not decide what tested means; the project's configuration and its existing
scripts do", yet the configuration schema has no test-command field and forbids adding one.
Both are now true: the agent chooses what to run, the factory records what ran and what it
returned, and neither interprets it. No schema field is needed and the forbidden line holds.

The prose note is the only part a model authors, and it is optional by construction — a round
that omits it loses a sentence, not its record.

The board can therefore trust that a result reflects what is actually on disk rather than
what the agent believed it had done. That matters most for the diff, which is what a reviewer
is actually judging.
