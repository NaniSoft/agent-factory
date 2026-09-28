#!/usr/bin/env bash
# The "tests" of the synthetic project the smoke test builds in.
#
# Passing and failing on purpose, so the round's result can show an exit code of
# 0 and a non-zero one. The factory derives test outcomes from exit codes
# (ADR-0011); it does not read a test report, and this proves that.

if [ ! -f src/index.js ]; then
  echo "test: src/index.js is missing" >&2
  exit 2
fi
if ! grep -q "answer" src/index.js; then
  echo "test: expected src/index.js to define an answer" >&2
  exit 1
fi
echo "test: 1 passed, 0 failed"
