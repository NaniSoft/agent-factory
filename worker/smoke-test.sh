#!/usr/bin/env bash
#
# The worker image smoke test.
#
# What it proves, and what it refuses to take on trust:
#
#   * the image's declared properties, read back off the built image rather than
#     off the Dockerfile that claims them
#   * a real round really runs, through the base image's own entrypoint, in a
#     container created with no -p and no -v
#   * the result file really comes back out with `docker cp`, is well-formed
#     JSON lines, and holds what ADR-0010 and ADR-0011 say it must
#   * the round's commit comes back out too, because ADR-0006 depends on it
#   * the container log carries the round's output as it happens
#
# What it does NOT do: call a model. An LLM is not what is under test, and a test
# that needs one tests the API key. The round is a script that behaves the way an
# agent behaves -- runs commands, changes files, commits, writes a note.
#
#   ./worker/smoke-test.sh                  build if needed, then run
#   ./worker/smoke-test.sh --rebuild        force the images to be rebuilt
#   ./worker/smoke-test.sh --image TAG      test a different base image
#   ./worker/smoke-test.sh --keep           leave the container and temp files
#   ./worker/smoke-test.sh --self-check     prove the test can fail
#
# --self-check is the important one. It runs this same suite against a
# deliberately broken image that can run a command and cannot return a result,
# and fails unless the suite goes red. The round-output property is the one most
# likely to be quietly faked, so it is the one worth breaking on purpose.

set -uo pipefail

here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

# The image name is the one a project file names. `worker/examples/*.project.yaml` and
# src/agent-factory.tests/Boundary/ProjectFile.cs both already say
# `ghcr.io/nanisoft/agent-factory-worker:1`, and this is that image, built
# locally under the same name so the two cannot drift apart.
BASE_IMAGE="${AGENT_FACTORY_BASE_IMAGE:-ghcr.io/nanisoft/agent-factory-worker:1}"
FIXTURE_IMAGE="${AGENT_FACTORY_SMOKE_IMAGE:-ghcr.io/nanisoft/agent-factory-worker-smoke:1}"
BROKEN_IMAGE="${AGENT_FACTORY_SMOKE_BROKEN_IMAGE:-ghcr.io/nanisoft/agent-factory-worker-broken:1}"
RESULT_PATH_IN_CONTAINER=/out/result.json
WORK_PATH_IN_CONTAINER=/work

checks=0
failures=0
cid=''
tmpdir=''

cleanup() {
    [ -n "$cid" ] && docker rm -f "$cid" >/dev/null 2>&1
    if [ -n "$tmpdir" ] && [ -d "$tmpdir" ]; then
        if [ "${KEEP:-0}" -eq 1 ]; then
            printf 'smoke-test: kept %s\n' "$tmpdir"
        else
            rm -rf -- "$tmpdir"
        fi
    fi
    return 0
}

# --- assertions -------------------------------------------------------------

ok()  { checks=$((checks + 1)); printf '  ok    %s\n' "$1"; }
bad() {
    checks=$((checks + 1)); failures=$((failures + 1))
    printf '  FAIL  %s\n' "$1"
    [ -n "${2:-}" ] && printf '          expected: %s\n' "$2"
    [ -n "${3:-}" ] && printf '          actual:   %s\n' "$3"
    return 0
}
eq()  { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "$3" "$2"; fi; }
neq() { if [ "$2" != "$3" ]; then ok "$1"; else bad "$1" "not $3" "$2"; fi; }
has() { case "${2:-}" in *"$3"*) ok "$1" ;; *) bad "$1" "contains: $3" "${2:-<empty>}" ;; esac; }
section() { printf '\n== %s\n' "$1"; }

# jq, run inside the image so the host needs nothing installed. The result file
# arrives on stdin; no mount is involved, because a worker container gets no host
# paths (ADR-0010) and neither does the test that checks it.
JQ_FACTS='
def cmds: [.[] | select(.kind == "command")];
def gitf($f): ([.[] | select(.kind == "git" and .field == $f) | .text] | first);
def txt($f): (gitf($f) // "");
def note: ([.[] | select(.kind == "note") | .text] | first);
[.[] ] as $a
| . as $all
| (cmds) as $c
| (($a[0]) // {}) as $h
| [
    "lines="                  + ($a | length | tostring),
    "headerKind="             + ($h.kind // "none"),
    "schema="                 + ($h.schema // "none"),
    "user="                   + ($h.user // "none"),
    "uid="                    + (($h.uid // -1) | tostring),
    "gitRepo="                + (($h.gitRepo // false) | tostring),
    "startHead="              + ($h.startHead // "none"),
    "head="                   + ($h.head // "none"),
    "roundExitCode="          + (($h.roundExitCode // -1) | tostring),
    "credentialEnvNames="     + (($h.credentialEnvNames // []) | join(",")),
    "git="                    + ($h.git // "none"),
    "opencode="               + ($h.opencode // "none"),
    "codeServer="             + ($h.codeServer // "none"),
    "commandCount="           + ($c | length | tostring),
    "seqAscending="           + (([ $c[] | .seq ] == ([ $c[] | .seq ] | sort)) | tostring),
    "firstArgv="              + (($c[0].argv // []) | join(" ")),
    "firstExit="              + (($c[0].exitCode // -99) | tostring),
    "nonZeroExits="           + ([ $c[] | select(.exitCode != 0)
                                  | ((.argv | join(" ")) + "=" + (.exitCode | tostring)) ] | join(" | ")),
    "outputCaptured="         + ([ $c[] | .stdoutBytes // 0 ] | (if length == 0 then false else (max > 0) end) | tostring),
    "note="                   + ((note // "none") | gsub("\n"; " ")),
    "statusSeesUntracked="    + (txt("status")               | test("scratch[.]tmp") | tostring),
    "statusSeesCommittedFile=" + (txt("status")              | test("src/index[.]js") | tostring),
    "roundDiffHasFile="       + (txt("diffFromRoundStart")   | test("src/index[.]js") | tostring),
    "roundDiffHasAnswer="     + (txt("diffFromRoundStart")   | test("answer")         | tostring),
    "roundDiffHasScratch="    + (txt("diffFromRoundStart")   | test("scratch[.]tmp")  | tostring),
    "commitsSinceStart="      + ((txt("commitsSinceRoundStart") | split("\n") | map(select(length > 0)) | length) | tostring),
    "remotes="                + (txt("remotes") | gsub("[\n\r\t]"; "") | if length == 0 then "none" else . end),
    "listenerCount="          + ([ $all[] | select(.kind == "listener") ] | length | tostring),
    "mentionsResultPath="     + ([ $all[] | select(.kind == "git")
                                  | select((.text // "") | test("result[.]json|commands[.]ndjson")) ]
                                  | length | tostring),
    "hasEndRecord="           + ([ $all[] | select(.kind == "end") ] | length | tostring)
  ][]
'

jqf() { docker run --rm -i --entrypoint jq "$BASE_IMAGE" -s -r "$JQ_FACTS" < "$1" 2>&1; }
fact() { printf '%s\n' "$2" | sed -n "s/^$1=//p" | head -1; }
inspected() { docker inspect --format "$1" "$cid" 2>/dev/null; }

# --- the suite --------------------------------------------------------------

run_suite() {
    local rebuild="$1"

    printf 'worker image smoke test\n'
    printf '  base image     %s\n' "$BASE_IMAGE"
    printf '  fixture image  %s\n' "$FIXTURE_IMAGE"
    printf '  docker server  %s\n' "$(docker version --format '{{.Server.Version}}' 2>/dev/null || echo unknown)"

    section 'the image as built, not as described'
    if [ "$rebuild" -eq 1 ] || ! docker image inspect "$BASE_IMAGE" >/dev/null 2>&1; then
        printf '  building %s (one 310 MB download, then cached)\n' "$BASE_IMAGE"
        docker build -t "$BASE_IMAGE" -f "$here/Dockerfile" "$here" >/dev/null || {
            printf 'smoke-test: the base image did not build\n' >&2; return 1; }
    fi

    local cfg_user cfg_ports img_size
    cfg_user=$(docker image inspect --format '{{.Config.User}}' "$BASE_IMAGE")
    cfg_ports=$(docker image inspect --format '{{json .Config.ExposedPorts}}' "$BASE_IMAGE")
    img_size=$(docker image inspect --format '{{.Size}}' "$BASE_IMAGE")

    neq 'the image names a user'  "$cfg_user" ''
    case "$cfg_user" in 0|root|root:*)
        bad 'the image runs as an unprivileged user' 'not root' "$cfg_user" ;;
    *) ok 'the image runs as an unprivileged user' ;;
    esac
    eq   'the image exposes no ports' "$cfg_ports" 'null'
    printf '  info  image size %s bytes, default user %s\n' "$img_size" "$cfg_user"

    section 'one round, in one container'
    tmpdir=$(mktemp -d) || { printf 'smoke-test: cannot make a temp dir\n' >&2; return 1; }
    # Git Bash hands Windows programs Windows paths; Linux has no cygpath.
    local tmpdir_win="${tmpdir}"
    command -v cygpath >/dev/null 2>&1 && tmpdir_win=$(cygpath -w "$tmpdir")

    if [ "$rebuild" -eq 1 ] || ! docker image inspect "$FIXTURE_IMAGE" >/dev/null 2>&1; then
        printf '  building %s\n' "$FIXTURE_IMAGE"
        docker build -t "$FIXTURE_IMAGE" --build-arg "BASE_IMAGE=$BASE_IMAGE" \
            -f "$here/smoke/Dockerfile" "$here/smoke" >/dev/null || {
            printf 'smoke-test: the fixture image did not build\n' >&2; return 1; }
    fi

    cid=$(docker create --name "agent-factory-smoke-$$-$RANDOM" "$FIXTURE_IMAGE")
    printf '  container %s\n' "$cid"

    # No -p, no -P, no -v, no --mount anywhere on this line, on purpose. The
    # claims "publishes no ports" and "mounts no host paths" are not properties
    # of the image alone; they are properties of the image *and* of this command.
    docker start -a "$cid" > "$tmpdir/container.log" 2>&1
    local container_exit=$?
    eq 'the container exited 0' "$container_exit" '0'

    docker cp "$cid:$RESULT_PATH_IN_CONTAINER" "$tmpdir_win/result.json" 2>"$tmpdir/cp.err"
    local cp_status=$?
    eq 'docker cp lifted the result file out' "$cp_status" '0'
    if [ -s "$tmpdir/result.json" ]; then
        ok 'the result file came back and is not empty'
    else
        bad 'the result file came back and is not empty' 'non-empty' \
            "absent or empty: $(tr -d '\r' < "$tmpdir/cp.err" 2>/dev/null | tr '\n' ' ')"
    fi

    section 'how the container was actually run'
    eq 'no host path was mounted into the container' "$(inspected '{{len .Mounts}}')" '0'
    eq 'no path was bind-mounted'                     "$(inspected '{{json .HostConfig.Binds}}')" 'null'
    eq 'no port was published'                         "$(inspected '{{len .HostConfig.PortBindings}}')" '0'
    eq 'no ports were exposed'                         "$(inspected '{{json .Config.ExposedPorts}}')" 'null'
    eq 'the container ran as the image user'           "$(inspected '{{.Config.User}}')" 'agent:agent'
    eq 'the container is not privileged'               "$(inspected '{{.HostConfig.Privileged}}')" 'false'
    eq 'the container has no added capabilities'       "$(inspected '{{json .HostConfig.CapAdd}}')" 'null'
    neq 'the container is not on the host network'     "$(inspected '{{.HostConfig.NetworkMode}}')" 'host'
    neq 'the container is not on the host PID namespace' "$(inspected '{{.HostConfig.PidMode}}')" 'host'

    section 'the entrypoint dispatches, and the tools run'
    # `agent` is the mode the factory's v1 adapter drives (ADR-0004). If it fails
    # to prepend the CLI it silently runs the wrong command instead of failing.
    ag_help=$(docker run --rm "$BASE_IMAGE" agent run --help 2>&1)
    has 'agent mode really runs the OpenCode CLI' "$ag_help" 'opencode run [flags]'
    eq  'the CLI is runnable non-interactively' \
        "$(docker run --rm "$BASE_IMAGE" exec opencode --version 2>/dev/null | tr -d '\r' | head -1)" \
        'opencode v2.0.18'
    # code-server must be present without being started: a version line, no
    # process, no socket.
    has 'code-server is runnable without being started' \
        "$(docker run --rm "$BASE_IMAGE" exec code-server --version 2>&1 | tr -d '\r')" \
        '4.139.1'

    section 'the result file'
    if [ -s "$tmpdir/result.json" ]; then
        local facts
        facts=$(jqf "$tmpdir/result.json")
        printf '%s\n' "$facts" | sed 's/^/  | /'

        eq  'the result is JSON lines of the agreed schema' "$(fact schema "$facts")" 'agent-factory/worker-result@1'
        eq  'the first line is the header'                 "$(fact headerKind "$facts")" 'result'
        eq  'the last line is the trailer'                 "$(fact hasEndRecord "$facts")" '1'
        neq 'the result is more than one line'             "$(fact lines "$facts")" '1'
        eq  'the round ran as uid 1000'                    "$(fact uid "$facts")" '1000'
        eq  'the round ran as the agent user'              "$(fact user "$facts")" 'agent'
        eq  'the collector found a git repository'         "$(fact gitRepo "$facts")" 'true'
        neq 'a base commit was captured before the round'  "$(fact startHead "$facts")" 'none'
        neq 'the round ended on a different commit'        "$(fact head "$facts")" "$(fact startHead "$facts")"
        eq  'the round itself exited 0'                    "$(fact roundExitCode "$facts")" '0'
        # 6 steps inside the round script, plus the round script itself, plus the
        # command the image's CMD hands to the entrypoint. Every one of them is
        # recorded, nested or not.
        eq  'every command the round ran was recorded'     "$(fact commandCount "$facts")" '8'
        eq  'the recorded sequence increases down the file' "$(fact seqAscending "$facts")" 'true'
        eq  'the first record is the first command it ran' "$(fact firstArgv "$facts")" './scripts/test.sh'
        eq  'a passing command is recorded as exit 0'      "$(fact firstExit "$facts")" '1'
        has 'a failing command is recorded with its code'  "$(fact nonZeroExits "$facts")" 'exit 3'
        has 'the earlier failure is recorded too'          "$(fact nonZeroExits "$facts")" '=1'
        eq  'the command output was captured, not dropped' "$(fact outputCaptured "$facts")" 'true'
        has 'the agent note came through'                  "$(fact note "$facts")" 'Added the answer to src/index.js'
        eq  'git status saw the file the round left behind' "$(fact statusSeesUntracked "$facts")" 'true'
        eq  'git status is clean of what the round committed' "$(fact statusSeesCommittedFile "$facts")" 'false'
        eq  'the round diff names the changed file'         "$(fact roundDiffHasFile "$facts")" 'true'
        eq  'the round diff shows what actually changed'    "$(fact roundDiffHasAnswer "$facts")" 'true'
        eq  'the round diff does not pretend the scratch file is a change' "$(fact roundDiffHasScratch "$facts")" 'false'
        eq  'the round commit is in the result'             "$(fact commitsSinceStart "$facts")" '1'
        eq  'the round holds no remote to push to'           "$(fact remotes "$facts")" 'none'
        eq  'the round was handed no credentials'           "$(fact credentialEnvNames "$facts")" ''
        eq  'nothing was listening inside the container'    "$(fact listenerCount "$facts")" '0'
        eq  'the result never mentions its own paths'       "$(fact mentionsResultPath "$facts")" '0'
        has 'git is in the image'                           "$(fact git "$facts")" 'git version'
        has 'the OpenCode CLI is in the image'              "$(fact opencode "$facts")" 'v2.'
        has 'code-server is in the image'                   "$(fact codeServer "$facts")" '4.'
    else
        bad 'the result file could be read' 'readable' 'absent'
    fi

    section 'the other channel, and the commit'
    if [ -s "$tmpdir/container.log" ]; then
        local logtext
        logtext=$(tr -d '\r' < "$tmpdir/container.log")
        has 'the log says where the result file is'     "$logtext" "result=$RESULT_PATH_IN_CONTAINER"
        has 'the log says how the round ended'          "$logtext" 'roundExitCode=0'
        has 'the log says what the round started from'  "$logtext" 'startHead='
        has 'the log carries what the passing test said'  "$logtext" 'test: 1 passed, 0 failed'
        has 'the log carries what the failing test said' "$logtext" 'expected src/index.js to define an answer'
    else
        bad 'the container log was captured' 'non-empty' 'empty'
    fi

    docker cp "$cid:$WORK_PATH_IN_CONTAINER" "$tmpdir_win/tree" 2>/dev/null
    local tree_status=$?
    eq 'the round tree comes back out with docker cp too' "$tree_status" '0'
    if [ -d "$tmpdir/tree" ]; then
        if [ -f "$tmpdir/tree/src/index.js" ]; then
            ok 'the tree has the file the round changed'
        else
            bad 'the tree has the file the round changed' 'present' 'absent'
        fi
        if diff -q "$tmpdir/tree/src/index.js" "$here/smoke/expected/index.js" >/dev/null 2>&1; then
            ok 'the file on disk is what the round said it wrote'
        else
            bad 'the file on disk is what the round said it wrote' \
                "$(tr '\n' ' ' < "$here/smoke/expected/index.js")" \
                "$(tr '\n' ' ' < "$tmpdir/tree/src/index.js" 2>/dev/null)"
        fi
        if command -v git >/dev/null 2>&1; then
            local head
            head=$(git -C "$tmpdir/tree" log --oneline -1 2>/dev/null)
            if [ -n "$head" ]; then
                ok 'the host can read the round commit out of the lifted tree'
                printf '  info  %s\n' "$head"
            else
                bad 'the host can read the round commit out of the lifted tree' 'a commit' 'none'
            fi
        else
            [ -f "$tmpdir/tree/.git/HEAD" ] \
                && ok 'the tree carries its git history' \
                || bad 'the tree carries its git history' 'present' 'absent'
        fi
    else
        bad 'the tree came back out' 'present' 'absent'
    fi

    printf '\n'
    if [ "$failures" -eq 0 ]; then
        printf 'PASS  %s checks\n' "$checks"
        return 0
    fi
    printf 'FAIL  %s of %s checks failed\n' "$failures" "$checks"
    return 1
}

# --- main -------------------------------------------------------------------

rebuild=0
KEEP=0
self_check=0
while [ $# -gt 0 ]; do
    case "$1" in
        --rebuild)    rebuild=1; shift ;;
        --keep)       KEEP=1; shift ;;
        --image)      BASE_IMAGE="$2"; FIXTURE_IMAGE="${2}-smoke"; BROKEN_IMAGE="${2}-broken"; shift 2 ;;
        --self-check) self_check=1; shift ;;
        -h|--help)    sed -n '3,25p' "$0" | sed 's/^#\{0,1\} \{0,1\}//'; exit 0 ;;
        *) printf 'smoke-test: unknown option %s\n' "$1" >&2; exit 64 ;;
    esac
done
export KEEP
trap cleanup EXIT

if [ "$self_check" -eq 1 ]; then
    # A broken image that can run a command and cannot return a result. The
    # suite must go red on it, or the suite is decorative.
    if [ "$rebuild" -eq 1 ] || ! docker image inspect "$BROKEN_IMAGE" >/dev/null 2>&1; then
        printf 'building the deliberately broken image %s\n' "$BROKEN_IMAGE"
        docker build -t "$BROKEN_IMAGE" --build-arg "BASE_IMAGE=$BASE_IMAGE" \
            -f "$here/smoke/Dockerfile.broken" "$here/smoke" >/dev/null || {
            printf 'smoke-test: the broken image did not build\n' >&2; exit 1; }
    fi

    printf '\n########## self-check: the same suite, against a broken image\n'
    printf '########## it MUST fail. If it passes, the test is decorative.\n\n'
    saved_base="$BASE_IMAGE"; saved_fixture="$FIXTURE_IMAGE"
    BASE_IMAGE="$BROKEN_IMAGE"; FIXTURE_IMAGE="$BROKEN_IMAGE"
    KEEP=0
    run_suite 0
    suite_status=$?
    BASE_IMAGE="$saved_base"; FIXTURE_IMAGE="$saved_fixture"

    printf '\n'
    if [ "$suite_status" -ne 0 ] && [ "$failures" -gt 0 ]; then
        printf 'SELF-CHECK PASS  the suite went red on a broken image (%s checks failed)\n' "$failures"
        printf '                    so a green run against the real image means something.\n'
        exit 0
    fi
    printf 'SELF-CHECK FAIL  the suite passed against an image that cannot return a result.\n'
    printf '                  The test is not testing what it claims to test.\n'
    exit 1
fi

run_suite "$rebuild"
