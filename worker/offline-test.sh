#!/usr/bin/env bash
#
# The worker image's scripts, tested without Docker.
#
# worker/smoke-test.sh is the authoritative test of the image: it builds it, runs a round
# in it, and reads back what came out. This is the other half, and it exists because of
# what happened in #41.
#
# Between the image's first commit and #41, three of the four files worker/Dockerfile
# copies were not in the repository. The image still existed on the machine that had it,
# so every question of the form "is the image there?" was answered yes, and the image's
# own smoke test — the one test that would have caught it — was the test that could not
# run, because it needs the daemon. A machine with Docker was the only machine where the
# build could be tried at all, which meant the build's inputs had no cheaper check than
# an expensive one, and none was written.
#
# So this runs the repository's three scripts directly, over real git repositories, and
# checks what they wrote. It needs `bash` and `git` and nothing else: no daemon, no image,
# no network, no model, and no sleeps beyond the one that proves output is streamed rather
# than buffered. That makes it the suite a reviewer can run anywhere, including on the
# machine that has no Docker.
#
# What it is not: it is not a substitute for the smoke test. It cannot check what the
# *image* installs — that the scripts are executable, that the CLI and code-server are
# present, that the container publishes no ports and mounts no host path, that `docker cp`
# lifts the result out. Those are properties of a built image and need a daemon. What it
# can check is the half that goes wrong silently: what the scripts emit, in what order,
# with which numbers, and whether the host can read it back.
#
#   worker/offline-test.sh
#
# Exit status is the number of failed checks, capped at 125, as `smoke-test.sh` does.

set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="${AGENT_FACTORY_OFFLINE_ROOT:-/tmp/agent-factory-offline-test}"

for required in bash git sed grep; do
    command -v "$required" >/dev/null 2>&1 || {
        printf 'offline-test: %s is not on this machine'"'"'s PATH, so this suite cannot run\n' "$required" >&2
        exit 2
    }
done

checks=0
failures=0

ok() { checks=$(( checks + 1 )); printf '  ok    %s\n' "$1"; }
bad() { checks=$(( checks + 1 )); failures=$(( failures + 1 )); printf '  FAIL  %s\n' "$1"; }
eq() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "expected [$3]" "actual [$2]"; fi; }
has() { case "$2" in *"$3"*) ok "$1" ;; *) bad "$1" "expected to contain [$3]" ;; esac; }
hasnt() { case "$2" in *"$3"*) bad "$1" "expected NOT to contain [$3]" ;; *) ok "$1" ;; esac; }
section() { printf '\n%s\n' "$1"; }

# --- the scripts, installed the way the image installs them ------------------------
#
# `run` and `worker-collect` source their JSON helpers from an absolute path, because in
# the image that is where the Dockerfile puts them and there is nothing to configure. It is
# also the one thing that cannot be reproduced here: `/usr/local` is not writable on this
# machine. So the copies under test have that single line rewritten to the repository's own
# worker/lib, and nothing else. The argument parsing, the capture, the sequence counter,
# the git observations, the exit codes and the record format are the repository's.
mkdir -p "$root/lib"
cp "$here/lib/json.sh" "$root/lib/json.sh"
for script in run worker-collect; do
    LC_ALL=C sed "s#^\\. /usr/local/lib/agent-factory/json.sh#. $root/lib/json.sh#" \
        "$here/bin/$script" > "$root/$script" || exit 2
    chmod +x "$root/$script" || exit 2
done
cp "$here/bin/worker-round" "$root/worker-round" || exit 2
chmod +x "$root/worker-round" || exit 2
export PATH="$root:$PATH"

work="$root/work"
out="$root/out"
export AGENT_FACTORY_WORK="$work"
export AGENT_FACTORY_OUT="$out"
export AGENT_FACTORY_RESULT="$out/result.json"
export AGENT_FACTORY_COMMAND_LOG="$out/commands.ndjson"
export AGENT_FACTORY_NOTE="$out/note.md"
export AGENT_FACTORY_SCHEMA=agent-factory/worker-result@1
unset AGENT_FACTORY_START_HEAD AGENT_FACTORY_ROUND_EXIT_CODE 2>/dev/null || true

# The container is given /work and /out and they exist before anything runs; the
# repository inside /work is the round's to make.
mkdir -p "$work" "$out"

# Empty the command log *and* the sequence counter. The counter is a file beside the log
# in the image, so truncating one and not the other is not a state the image is ever in.
empty_log() { : > "$AGENT_FACTORY_COMMAND_LOG"; rm -f "$out/.run-seq"; }

printf 'worker offline test\n'
printf '  scripts %s\n' "$here/bin"
printf '  scratch %s\n' "$root"

# ================================================================================
# run, the recording wrapper
# ================================================================================

# Read one field off one command record. A grep-and-cut rather than a parser, because
# each check is about a single field and a reader that could mis-parse would be a worse
# instrument than one that cannot.
run_raw() { grep '"kind":"command"' "$AGENT_FACTORY_COMMAND_LOG" | sed -n "${1}p"; }
run_field() { run_raw "$1" | LC_ALL=C sed -n "s/.*\"$2\":\"\\([^\"]*\\)\".*/\\1/p"; }
run_num() { run_raw "$1" | LC_ALL=C sed -n "s/.*\"$2\":\\([-0-9]*\\).*/\\1/p"; }
run_bare() { run_raw "$1" | LC_ALL=C sed -n "s/.*\"$2\":\\([a-z]*\\).*/\\1/p"; }
run_count() {
    local n
    n=$(grep -c '"kind":"command"' "$AGENT_FACTORY_COMMAND_LOG" 2>/dev/null)
    printf '%s' "${n:-0}"
}
# Whether the sequence numbers increase down the file — the property the deriver reads
# records in order for. A wrapper that nests wrongly still records both commands; it just
# records them out of order.
run_ascending() {
    local previous=0 current
    while read -r current; do
        case "$current" in ''|*[!0-9]*) return 1 ;; esac
        [ "$current" -le "$previous" ] && return 1
        previous="$current"
    done < <(grep '"kind":"command"' "$AGENT_FACTORY_COMMAND_LOG" \
        | LC_ALL=C sed -n 's/.*"seq":\([0-9]*\).*/\1/p')
    return 0
}

section 'run: the recording wrapper'

# --- the command's own exit code, always ------------------------------------------
# The whole of the wrapper's contract with its caller. A wrapper that returned something
# else would make every exit code in the result file a statement about the wrapper.
empty_log
run -o 'a passing command' bash -c 'echo one; echo two >&2' >/dev/null 2>&1
eq 'a passing command exits 0' "$?" '0'

run -o 'a failing command' bash -c 'echo bad >&2; exit 3' >/dev/null 2>&1
eq 'a failing command exits with its own code' "$?" '3'

run -o 'missing' definitely-not-a-command-abcdef >/dev/null 2>&1
eq 'a command that is not there returns 127' "$?" '127'

# --- the record -------------------------------------------------------------------
eq 'all three commands were recorded' "$(run_count)" '3'
eq 'the first label is recorded' "$(run_field 1 label)" 'a passing command'
eq 'the first exit code is recorded' "$(run_num 1 exitCode)" '0'
eq "a failing command records its own code" "$(run_num 2 exitCode)" '3'
eq "a command that is not there is recorded too" "$(run_num 3 exitCode)" '127'
eq 'the sequence starts at 1' "$(run_num 1 seq)" '1'
eq 'the sequence increases down the file' "$(run_num 3 seq)" '3'

# --- both streams are captured, separately -----------------------------------------
has 'stdout is captured into the record' "$(run_field 1 stdoutTail)" 'one'
has 'stderr is captured into the record' "$(run_field 1 stderrTail)" 'two'
hasnt 'the streams are not merged' "$(run_field 1 stdoutTail)" 'two'
hasnt 'nor the other way round' "$(run_field 1 stderrTail)" 'one'
[ "$(run_num 1 stdoutBytes)" -gt 0 ] && ok 'stdout bytes are counted' || bad 'stdout bytes are counted'

# --- arguments are recorded verbatim -----------------------------------------------
empty_log
run -o 'arguments' bash -c 'true' --flag 'a value with spaces' >/dev/null 2>&1
record="$(run_raw 1)"
has 'an argument with spaces stays one argument' "$record" '"a value with spaces"'
eq 'and the argv holds every argument' \
    "$(printf '%s' "$record" | LC_ALL=C sed -n 's/.*"argv":\(\[[^]]*\]\).*/\1/p' | tr ',' '\n' | wc -l | tr -d ' ')" '4'

# --- wrappers nest, and the sequence increases through a nesting --------------------
# A round that runs a script which itself calls `run` records both. The number is
# assigned when the record is *written*, so it increases down the file whichever way the
# commands were nested.
empty_log
cat > "$root/inner.sh" <<'INNER'
#!/usr/bin/env bash
run -o 'the inner command' bash -c 'echo inner'
INNER
chmod +x "$root/inner.sh"
run -o 'the outer command' bash "$root/inner.sh" >/dev/null 2>&1
eq 'a nested wrapper records both commands' "$(run_count)" '2'
if run_ascending; then ok 'and the sequence increases through a nesting'
else bad 'and the sequence increases through a nesting'; fi
eq 'and the inner command finished first' "$(run_field 1 label)" 'the inner command'

# --- output is streamed, not buffered -----------------------------------------------
# ADR-0010 makes `docker logs` the channel carrying a round's live progress, so a
# ninety-minute round must not look silent until it ends.
empty_log
streamed="$(run -o 'streamed' bash -c 'echo early; sleep 1; echo late' 2>/dev/null)"
has 'output is streamed as the command runs' "$streamed" 'early'
has 'and the end of the output arrives too' "$streamed" 'late'

# --- capture can be switched off ---------------------------------------------------
empty_log
AGENT_FACTORY_CAPTURE_OUTPUT=0 run -o 'uncaptured' bash -c 'echo quiet' >/dev/null 2>&1
eq 'an uncaptured command says so' "$(run_bare 1 captured)" 'false'
eq 'and records no tail' "$(run_field 1 stdoutTail)" ''

# --- the tail is bounded, and it is the end ----------------------------------------
# The bound is on what is *kept*, so it is checked by counting what arrived rather than
# by measuring a length: a tail of raw bytes is longer in JSON, because every newline is
# two bytes once escaped, so a length would be measuring the escaper.
empty_log
run -o 'a loud command' bash -c 'for i in $(seq 1 1000); do echo "line $i"; done' >/dev/null 2>&1
[ "$(run_num 1 stdoutBytes)" -gt 6000 ] && ok 'the full output is measured' || bad 'the full output is measured'
tail_lines="$(run_field 1 stdoutTail | grep -c 'line ')"
[ "$tail_lines" -gt 0 ] && [ "$tail_lines" -lt 1000 ] \
    && ok 'the recorded tail is bounded, not the whole output' \
    || bad 'the recorded tail is bounded, not the whole output' "lines=$tail_lines"
has 'and the bounded tail is the end of the output' "$(run_field 1 stdoutTail)" 'line 1000'

# --- quoting that would break a naive escaper ---------------------------------------
# Read off the raw record rather than through a field reader: the whole point is what the
# *escaped* bytes look like, and un-escaping them on the way out would hide the defect.
empty_log
run -o 'hostile quoting' bash -c 'printf "a quote \" and a backslash \\ and a tab\t" >&2' >/dev/null 2>&1
record="$(run_raw 1)"
has 'a quote is escaped in the record' "$record" 'a quote \"'
has 'a backslash is doubled in the record' "$record" 'backslash \\\\'

# --- arguments are refused rather than guessed at -------------------------------------
run >/dev/null 2>&1
eq 'run with no command is refused' "$?" '64'
run -o >/dev/null 2>&1
eq 'a label with no value is refused' "$?" '64'
run -C >/dev/null 2>&1
eq 'a directory with no value is refused' "$?" '64'
run --nonsense true >/dev/null 2>&1
eq 'an unknown option is refused' "$?" '64'

# `--` ends option parsing, so a command whose own name looks like one is still run
# rather than refused. A wrapper that ate it would make some real commands unreachable.
empty_log
run -- bash -c 'echo after the separator' >/dev/null 2>&1
eq 'a command after -- is run rather than refused' "$?" '0'
eq 'and it is the command that was recorded' "$(run_num 1 exitCode)" '0'
# An option value may begin with a dash — a flag to pass down, a negative number — which
# is the other half of the same question.
empty_log
run -e SOME_FLAG=--verbose bash -c 'exit 0' >/dev/null 2>&1
eq 'an option value may begin with a dash' "$?" '0'
has 'and is recorded on the command'"'"'s own record' "$(run_raw 1)" 'SOME_FLAG=--verbose'

# ================================================================================
# worker-collect, the collector
# ================================================================================

result_record() { sed -n "${1}p" "$AGENT_FACTORY_RESULT"; }
result_kind_at() { result_record "$1" | LC_ALL=C sed -n 's/.*"kind":"\([a-z]*\)".*/\1/p'; }
result_field() { result_record "$1" | LC_ALL=C sed -n "s/.*\"$2\":\"\\([^\"]*\\)\".*/\\1/p"; }
result_num() { result_record "$1" | LC_ALL=C sed -n "s/.*\"$2\":\\([-0-9]*\\).*/\\1/p"; }
result_bare() { result_record "$1" | LC_ALL=C sed -n "s/.*\"$2\":\\([a-z]*\\).*/\\1/p"; }
result_lines() { wc -l < "$AGENT_FACTORY_RESULT" | tr -d ' '; }
result_count() {
    local n
    n=$(grep -c "\"kind\":\"$1\"" "$AGENT_FACTORY_RESULT" 2>/dev/null)
    printf '%s' "${n:-0}"
}

# A repository the way a round leaves one, built by running git rather than by writing a
# fixture: a base commit, so the collector has something to diff from.
collect_seed_repo() {
    rm -rf "$work" "$out"
    mkdir -p "$work" "$out"
    git -C "$work" init -q -b main
    git -C "$work" config user.email round@example.invalid
    git -C "$work" config user.name 'a round'
    # The host pins this off for the same reason (see ADR-0026's sibling decision in
    # Results/HostDiffReader): the round committed in a container with autocrlf off, so a
    # diff taken on a host with it on describes the host rather than the change.
    git -C "$work" config core.autocrlf false
    printf 'the original file\n' > "$work/tracked.txt"
    git -C "$work" add tracked.txt
    git -C "$work" commit -q -m 'the commit the round started at'
    export AGENT_FACTORY_START_HEAD="$(git -C "$work" rev-parse HEAD)"
    rm -f "$AGENT_FACTORY_COMMAND_LOG" "$AGENT_FACTORY_NOTE" "$AGENT_FACTORY_RESULT"
}

section 'worker-collect: the result file the host lifts'

# --- a round that changed nothing and ran nothing ----------------------------------
collect_seed_repo
AGENT_FACTORY_ROUND_EXIT_CODE=0 worker-collect >/dev/null 2>&1
eq 'a collector that could run exits 0' "$?" '0'
[ -s "$AGENT_FACTORY_RESULT" ] && ok 'a result file is written' || bad 'a result file is written'
eq 'the header is line 1' "$(result_kind_at 1)" 'result'
eq 'it carries the schema the host expects' "$(result_field 1 schema)" 'agent-factory/worker-result@1'
eq 'it says the round ran to completion' "$(result_num 1 roundExitCode)" '0'
eq 'it says it is a repository' "$(result_bare 1 gitRepo)" 'true'
eq 'it records the start commit' "$(result_field 1 startHead)" "$AGENT_FACTORY_START_HEAD"
eq 'and the head it ended on' "$(result_field 1 head)" "$AGENT_FACTORY_START_HEAD"
eq 'the end record is last' "$(result_kind_at "$(result_lines)")" 'end'
eq 'and says the round ran no commands' "$(result_num "$(result_lines)" commandRecords)" '0'

# --- the change, uncommitted --------------------------------------------------------
# An untracked scratch file is the half of the change `git status` alone catches and a
# diff does not. Reading either alone silently drops half of what a reviewer judges.
collect_seed_repo
printf 'the original file\nplus a line\n' > "$work/tracked.txt"
printf 'scratch\n' > "$work/scratch.txt"
AGENT_FACTORY_ROUND_EXIT_CODE=0 worker-collect >/dev/null 2>&1
whole="$(cat "$AGENT_FACTORY_RESULT")"
has 'an untracked file is in the status record' "$whole" 'scratch.txt'
has 'a tracked change is in the status record' "$whole" 'tracked.txt'
eq 'the first git record is the status' \
    "$(grep '"kind":"git"' "$AGENT_FACTORY_RESULT" | head -1 \
        | LC_ALL=C sed -n 's/.*"field":"\([a-zA-Z]*\)".*/\1/p')" 'status'
diff_record="$(grep '"field":"diffFromRoundStart"' "$AGENT_FACTORY_RESULT")"
has 'the diff from the round start is recorded' "$diff_record" 'plus a line'
has 'and names the file it changed' "$diff_record" 'tracked.txt'
hasnt 'and does not claim the scratch file is a change' "$diff_record" 'scratch.txt'

# --- the change, committed ----------------------------------------------------------
collect_seed_repo
printf 'the original file\nplus a line\n' > "$work/tracked.txt"
git -C "$work" add tracked.txt
git -C "$work" commit -q -m 'the work'
AGENT_FACTORY_ROUND_EXIT_CODE=0 worker-collect >/dev/null 2>&1
commits="$(grep '"field":"commitsSinceRoundStart"' "$AGENT_FACTORY_RESULT")"
has 'a commit the round made is recorded' "$commits" 'the work'
hasnt 'and the commit it started from is not listed as its own' "$commits" "$AGENT_FACTORY_START_HEAD"

# --- the round's own exit code, and the two ways of failing -------------------------
# This is the #22 boundary. A command's exit code is data; the round's own is the only
# thing that decides whether the round ran to completion.
collect_seed_repo
AGENT_FACTORY_ROUND_EXIT_CODE=1 worker-collect >/dev/null 2>&1
eq 'a round that did not run to completion says so' "$(result_num 1 roundExitCode)" '1'
hasnt 'and is not reported as null' "$(head -1 "$AGENT_FACTORY_RESULT")" '"roundExitCode":null'

# Absent is not zero. Reading a missing field as a failure is the guessing #22 is about,
# and it parks a work item that was about to work.
collect_seed_repo
worker-collect >/dev/null 2>&1
has 'a round that has not said how it ended says null' \
    "$(head -1 "$AGENT_FACTORY_RESULT")" '"roundExitCode":null'

# --- the commands, copied from the wrapper -------------------------------------------
collect_seed_repo
run -o 'a passing command' bash -c 'echo fine' >/dev/null 2>&1
run -o 'a failing command' bash -c 'echo broken >&2; exit 2' >/dev/null 2>&1
AGENT_FACTORY_ROUND_EXIT_CODE=0 worker-collect >/dev/null 2>&1
eq 'the commands are carried over verbatim' "$(result_count command)" '2'
eq 'and the end record counts them' "$(result_num "$(result_lines)" commandRecords)" '2'
eq 'while the header still says the round completed' "$(result_num 1 roundExitCode)" '0'
eq "and the failing command's own code is on its own record" \
    "$(grep '"kind":"command"' "$AGENT_FACTORY_RESULT" | sed -n '2p' \
        | LC_ALL=C sed -n 's/.*"exitCode":\([-0-9]*\).*/\1/p')" '2'

# --- a damaged command log is the case JSON lines exist for -------------------------
# One truncated line: the log is non-empty, so a naive `grep -c ... || printf 0` prints
# two zeros and the record is split across two lines. The result has to survive it.
collect_seed_repo
printf '{"kind":"comm' > "$AGENT_FACTORY_COMMAND_LOG"
AGENT_FACTORY_ROUND_EXIT_CODE=0 worker-collect >/dev/null 2>&1
eq 'a truncated command log still counts zero commands' \
    "$(result_num "$(result_lines)" commandRecords)" '0'
eq 'and does not split the trailer across two lines' "$(result_lines)" \
    "$(grep -c '' "$AGENT_FACTORY_RESULT")"

# --- the note --------------------------------------------------------------------------
collect_seed_repo
printf 'I changed tracked.txt because the issue asked.\n' > "$AGENT_FACTORY_NOTE"
AGENT_FACTORY_ROUND_EXIT_CODE=0 worker-collect >/dev/null 2>&1
eq 'the agent note is one record' "$(result_count note)" '1'
has 'and is copied' "$(grep '"kind":"note"' "$AGENT_FACTORY_RESULT")" 'I changed tracked.txt'
# The note is optional. Deleting the file loses a sentence, not a record.
collect_seed_repo
rm -f "$AGENT_FACTORY_NOTE"
AGENT_FACTORY_ROUND_EXIT_CODE=0 worker-collect >/dev/null 2>&1
eq 'a round with no note still produces a result' "$(result_count result)" '1'
eq 'and simply has no note record' "$(result_count note)" '0'

# --- credentials: the names, never the values -----------------------------------------
collect_seed_repo
AGENT_FACTORY_MODEL='anthropic/claude-sonnet-4-5' \
ANTHROPIC_API_KEY='the-value-of-the-key' \
GITHUB_TOKEN='the-write-token' \
HOME="$root" \
    worker-collect >/dev/null 2>&1
header="$(result_record 1)"
has 'the LLM key name is recorded' "$header" 'ANTHROPIC_API_KEY'
has 'the write token name is recorded' "$header" 'GITHUB_TOKEN'
hasnt 'the key value is not' "$header" 'the-value-of-the-key'
hasnt 'the token value is not' "$header" 'the-write-token'
hasnt 'and no credential value is anywhere in the result' \
    "$(cat "$AGENT_FACTORY_RESULT")" 'the-write-token'
hasnt 'nor is HOME mistaken for one' "$header" 'HOME'

# --- no repository at all ----------------------------------------------------------------
collect_seed_repo
rm -rf "$work"
AGENT_FACTORY_START_HEAD=''
AGENT_FACTORY_ROUND_EXIT_CODE=0 worker-collect >/dev/null 2>&1
eq 'a round with no repository still produces a header' "$(result_kind_at 1)" 'result'
eq 'and says it had none' "$(result_bare 1 gitRepo)" 'false'
eq 'and is honest about having no base commit' "$(result_field 1 startHead)" ''
eq 'and records no git observations at all' "$(result_count git)" '0'

# --- unwritable result paths ---------------------------------------------------------------
# A file in the way of the result, rather than a missing directory: the collector makes its
# own directory, so this is what "cannot write" actually looks like. Without the refusal a
# round ends claiming a result nobody can lift.
collect_seed_repo
printf 'not a directory\n' > "$root/blocked"
AGENT_FACTORY_RESULT="$root/blocked/result.json" worker-collect >/dev/null 2>&1
eq 'a collector that cannot write returns 70' "$?" '70'
[ ! -e "$root/blocked/result.json" ] && ok 'and claims no result it did not write' \
                                     || bad 'and claims no result it did not write'
collect_seed_repo
mkdir -p "$root/result.json"
AGENT_FACTORY_RESULT="$root/result.json" worker-collect >/dev/null 2>&1
eq 'a result path that is a directory returns 70 too' "$?" '70'
rm -rf "$root/result.json"

# --- collecting twice ------------------------------------------------------------------------
collect_seed_repo
AGENT_FACTORY_ROUND_EXIT_CODE=0 worker-collect >/dev/null 2>&1
first="$(result_lines)"
AGENT_FACTORY_ROUND_EXIT_CODE=0 worker-collect >/dev/null 2>&1
eq 'collecting twice does not double the records' "$(result_lines)" "$first"
eq 'and leaves one header, not two' "$(result_count result)" '1'

# ================================================================================
# worker-round, the entrypoint
# ================================================================================

entry_field() { sed -n "1s/.*\"$1\":\"\\([^\"]*\\)\".*/\\1/p" "$AGENT_FACTORY_RESULT"; }
entry_num() { sed -n "1s/.*\"$1\":\\([-0-9]*\\).*/\\1/p" "$AGENT_FACTORY_RESULT"; }
entry_bare() { sed -n "1s/.*\"$1\":\\([a-z]*\\).*/\\1/p" "$AGENT_FACTORY_RESULT"; }
entry_count() {
    local n
    n=$(grep -c "\"kind\":\"$1\"" "$AGENT_FACTORY_RESULT" 2>/dev/null)
    printf '%s' "${n:-0}"
}

# Run the entrypoint, capturing both channels separately — which is what the host has: the
# log goes to `docker logs` and the exit code to `docker start -a`. No `cd` here; the
# entrypoint makes and enters its own working directory, and a harness that pre-created it
# would be testing a container whose /work already existed.
entrypoint() {
    worker-round "$@" > "$root/stdout" 2> "$root/stderr"
    code=$?
    log="$(cat "$root/stdout" "$root/stderr")"
    return 0
}

# A round script on disk rather than a `bash -c` string. The image's own CMD nests three
# levels of quoting, and a harness that reproduced that in a shell literal would be
# quoting rather than testing.
round_script() {
    cat > "$root/round.sh"
    chmod +x "$root/round.sh"
    printf '%s' "$root/round.sh"
}

entry_seed_repo() {
    rm -rf "$work" "$out"
    mkdir -p "$work" "$out"
    git -C "$work" init -q -b main
    git -C "$work" config user.email round@example.invalid
    git -C "$work" config user.name 'a round'
    git -C "$work" config core.autocrlf false
    printf 'export const answer = undefined;\n' > "$work/index.js"
    git -C "$work" add index.js
    git -C "$work" commit -q -m 'base of the synthetic project'
}

section 'worker-round: the entrypoint the image runs'

# --- help, which is the image's default CMD ------------------------------------------------
entry_seed_repo
entrypoint help
eq 'help exits 0' "$code" '0'
has 'help names the agent mode' "$log" 'worker-round agent run'
has 'help names exec' "$log" 'worker-round exec'
has 'help names collect' "$log" 'worker-round collect'
hasnt 'help does not spill the prose around the modes' "$log" 'Two things are deliberately'
hasnt 'nor the file'"'"'s own commentary' "$log" 'ADR-0001'
[ ! -e "$AGENT_FACTORY_RESULT" ] && ok 'help writes no result' || bad 'help writes no result'

entry_seed_repo
entrypoint
eq 'no arguments means help' "$code" '0'
has 'and says the same thing' "$log" 'worker-round agent run'

entry_seed_repo
entrypoint nonsense
eq 'an unknown mode is refused' "$code" '64'
has 'and the refusal names it' "$log" 'unknown mode nonsense'
hasnt 'and it runs nothing' "$log" 'roundExitCode='

# --- exec, the nesting the smoke fixture image's CMD produces --------------------------------
# Counted from the outside in: the entrypoint records the `exec` command (1), whose
# `bash -c` runs a round script through the wrapper (2), whose script runs two commands
# through it (3, 4). The `bash -c` is the exec wrapper's own argv rather than a wrapper of
# its own — which is what makes this four and not five, and what makes the smoke fixture's
# eight add up.
entry_seed_repo
script="$(round_script <<'ROUND'
run -o 'the first thing' bash -c 'exit 0'
run -o 'the second thing' bash -c 'sleep 1; exit 0'
ROUND
)"
entrypoint exec bash -c "run -o 'the round' bash $script"
eq 'a round that ran to completion exits 0' "$code" '0'
has 'the log says where the result is' "$log" "result=$AGENT_FACTORY_RESULT"
has 'the log says how the round ended' "$log" 'roundExitCode=0'
has 'the log says what it started from' "$log" 'startHead='
eq 'one header was written' "$(entry_count result)" '1'
eq 'and it records the round completing' "$(entry_num roundExitCode)" '0'
eq 'every command in the round was recorded, nested or not' "$(entry_count command)" '4'
eq 'and the sequence increases down the file' \
    "$(grep '"kind":"command"' "$AGENT_FACTORY_RESULT" | LC_ALL=C sed -n 's/.*"seq":\([0-9]*\).*/\1/p' | tr '\n' ' ')" '1 2 3 4 '

# The first record is the first command to *finish*, so a round's log reads inside-out.
# The smoke test depends on this: it asserts the first record is the baseline test, which
# is the earliest of a sequential round's commands.
eq 'and the first record is the command that finished first' \
    "$(grep '"kind":"command"' "$AGENT_FACTORY_RESULT" | head -1 \
        | LC_ALL=C sed -n 's/.*"label":"\([^"]*\)".*/\1/p')" 'the first thing'

# --- a round that did not run to completion -----------------------------------------------------
# #22: the exit code is *in* the result and the container still exits 0 because it wrote
# one. A factory reading `docker run`'s code as the round's would park a work item over a
# rate limit and read a refused provider as a finished build.
entry_seed_repo
entrypoint exec bash -c 'exit 3'
eq 'a round whose command failed still exits 0' "$code" '0'
eq "and the round's own failure is in the result" "$(entry_num roundExitCode)" '3'
has 'and the log says so' "$log" 'roundExitCode=3'
eq 'while the command record carries its own code' \
    "$(grep '"kind":"command"' "$AGENT_FACTORY_RESULT" | LC_ALL=C sed -n 's/.*"exitCode":\([-0-9]*\).*/\1/p')" '3'

# --- nowhere to write ----------------------------------------------------------------------------
entry_seed_repo
printf 'not a directory\n' > "$root/blocked"
AGENT_FACTORY_RESULT="$root/blocked/result.json" entrypoint exec bash -c 'exit 0'
eq 'a round with nowhere to write its result exits 70' "$code" '70'
has 'and says so' "$log" 'was not written'
rm -f "$root/blocked"

# --- collect on its own ----------------------------------------------------------------------------
# A round that has already ended by some other route still has to be answerable.
entry_seed_repo
run -o 'work done before collection' bash -c 'echo done' >/dev/null 2>&1
printf 'a note written by the round\n' > "$AGENT_FACTORY_NOTE"
entrypoint collect
eq 'collect on its own exits 0' "$code" '0'
eq 'and writes one header' "$(entry_count result)" '1'
eq 'and the command is in it' "$(entry_count command)" '1'
eq 'and the note is in it' "$(entry_count note)" '1'
# Nothing watched this round, so nothing said how it ended. Absent is not zero.
has 'and it does not invent a round exit code' "$(head -1 "$AGENT_FACTORY_RESULT")" '"roundExitCode":null'

# --- a result inside the working tree is refused ------------------------------------------------------
# A result inside the tree would appear in the diff the reviewer is judging. Checked
# before anything runs: checking it afterwards would cost the whole round.
entry_seed_repo
AGENT_FACTORY_RESULT="$work/result.json" entrypoint exec bash -c 'echo should not run'
eq 'a result inside the working tree is refused' "$code" '64'
has 'and the refusal names the path' "$log" 'is inside the working tree'
hasnt 'and the refused round runs nothing' "$log" 'roundExitCode='
[ ! -e "$work/result.json" ] && ok 'and writes no result there' || bad 'and writes no result there'

entry_seed_repo
AGENT_FACTORY_RESULT="$work/out/result.json" entrypoint exec bash -c 'echo should not run'
eq 'a result inside a subdirectory of the tree is refused too' "$code" '64'

# A path that merely shares a prefix with the tree is not inside it.
entry_seed_repo
AGENT_FACTORY_RESULT="$root/working/result.json" entrypoint exec bash -c 'exit 0'
eq 'a path that only shares a prefix is not refused' "$code" '0'

# --- a tree is fetched when one is asked for ----------------------------------------------------------------
# Over the network, unauthenticated: a round holds no write credential (ADR-0006), so a
# private repository is not something it can fetch. A real local repository stands in for
# a remote — a directory git accepts as one is a remote.
origin="$root/origin.git"
rm -rf "$origin"
git init -q --bare -b main "$origin"
entry_seed_repo
git -C "$work" remote add origin "$origin"
git -C "$work" push -q origin main
rm -rf "$work" "$out"
AGENT_FACTORY_REPO_URL="$origin" AGENT_FACTORY_BASE_REF=main entrypoint exec bash -c 'exit 0'
eq 'a round that fetched its own tree exits 0' "$code" '0'
has 'and the log says it cloned' "$log" 'cloned'
eq 'and the tree it cloned is a repository' "$(entry_bare gitRepo)" 'true'
eq 'and the remote it now holds is recorded' \
    "$(grep '"field":"remotes"' "$AGENT_FACTORY_RESULT" \
        | LC_ALL=C sed -n 's/.*"text":"\([^"]*\)".*/\1/p' | wc -l | tr -d ' ')" '1'

rm -rf "$work" "$out"
AGENT_FACTORY_REPO_URL="$origin" AGENT_FACTORY_BASE_REF=no-such-branch \
    entrypoint exec bash -c 'echo should not run'
eq 'a base ref that does not exist is refused' "$code" '66'
hasnt 'and the round does not run' "$log" 'roundExitCode='

rm -rf "$work" "$out"
entrypoint exec bash -c 'exit 0'
eq 'a round with no repository and nothing to clone still runs' "$code" '0'
eq 'and says it had none' "$(entry_bare gitRepo)" 'false'

# ================================================================================

printf '\n'
if [ "$failures" -eq 0 ]; then
    printf 'PASS  %d checks\n' "$checks"
    exit 0
fi

printf 'FAIL  %d of %d checks failed\n' "$failures" "$checks"
[ "$failures" -gt 125 ] && failures=125
exit "$failures"