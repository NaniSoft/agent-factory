#!/usr/bin/env bash
#
# What the agent does in one round, for the smoke test.
#
# This is deliberately not an agent. An LLM is not under test here, and a test
# that needs a model and an API key tests the key, not the image. What is under
# test is the machinery around it: that commands run, that their exit codes are
# recorded rather than interpreted, that the change lands in git and is diffed
# against the commit the round started from, that the round commits locally and
# never pushes, and that all of it comes back out in one file.
#
# ADR-0011: the factory derives the result by observing. Nothing below writes the
# result. The agent's whole contribution is the one prose note near the bottom.

set -uo pipefail
cd "$AGENT_FACTORY_WORK"

failures=0
step() { printf '\n=== %s\n' "$1"; }

step 'baseline check'
run -o 'the tests, before the change' ./scripts/test.sh
echo "unexpected: the baseline test passed" >&2
failures=$(( failures + 1 ))

step 'make the change'
run -o 'write the change' cp /opt/smoke/expected/index.js src/index.js

step 'the tests, after the change'
run -o 'the tests, after the change' ./scripts/test.sh

step 'a command that fails on purpose'
# The factory reads test outcomes from exit codes. A round whose last command
# failed must not look like a round whose last command succeeded, and the only
# thing that distinguishes them is this number.
run -o 'lint' bash -c 'echo "lint: 2 problems" >&2; exit 3'

step 'a scratch file the agent left behind'
# Agents leave scratch files. `git status` is how the factory sees them, and an
# uncommitted change like this one is invisible to `git diff`, so the two
# observations are not redundant -- each catches something the other misses.
run -o 'a scratch file' bash -c 'echo "scratch" > scratch.tmp'

step 'the agent note'
# The one thing the model authors, and it is optional: delete this file and the
# result loses a sentence, not its record.
printf 'Added the answer to src/index.js and made the project tests pass.\n' \
    > "$AGENT_FACTORY_NOTE"

step 'commit locally, never push'
# ADR-0006: the round ends at a commit. The host retrieves it and pushes from
# there. There is no credential in here that could push anything.
run -o 'commit the round' git commit -q -am 'answer the question'

printf '\nround finished with %s deliberate surprise(s)\n' "$failures"
exit 0
