# JSON emission helpers for the worker image.
#
# There is no jq dependency in the hot path here: `run` writes one record per
# command, potentially thousands of times, and shelling out to jq each time is
# wasted work. These are sed and tr, which are already present.
#
# The result file is JSON *lines*: one self-contained object per line. That is
# what makes a damaged result degrade to a partial one -- the host reads it line
# by line, and a truncated tail costs the tail and nothing else.

# Escape the body of a JSON string. Reads stdin, writes the escaped bytes.
#
# `sed -z` is the load-bearing part. The obvious alternative -- slurp with
# `:a; N; $!ba` and substitute afterwards -- silently escapes only the *first*
# line: sed runs the substitutions on the first line, then slurps, then runs the
# newline substitution over the whole buffer. A one-line payload with a quote in it
# comes out unescaped and the result file stops parsing. `sed -z` reads the whole
# input as one record first, so every substitution sees every byte, and it copes
# with a final line that has no trailing newline, which `N` does not.
#
# Order matters in the other direction too: backslashes are doubled first, and
# only then do the expressions that introduce fresh backslashes.
#
# C0 control characters that JSON forbids outright are dropped first, because
# there is no escape for them that every reader accepts.
json_escape() {
    LC_ALL=C tr -d '\000-\010\013\014\016-\037' \
    | LC_ALL=C sed -z \
        -e 's/\\/\\\\/g' \
        -e 's/"/\\"/g' \
        -e 's/\t/\\t/g' \
        -e 's/\r/\\r/g' \
        -e 's/\n/\\n/g'
}

# Emit a complete JSON string, quotes included, from stdin.
json_string() {
    printf '"%s"' "$(json_escape)"
}

# Emit a JSON string built from an argument rather than stdin.
json_lit() {
    printf '%s' "$1" | json_string
}

# Emit a JSON array of strings from the remaining arguments.
json_string_array() {
    local first=1 a
    printf '['
    for a in "$@"; do
        [ "$first" -eq 1 ] || printf ','
        first=0
        json_lit "$a"
    done
    printf ']'
}

# ISO-8601 UTC, seconds precision, from the epoch. Used instead of `date -Iseconds`
# so the image does not depend on a particular coreutils date implementation.
json_now() {
    date -u +%Y-%m-%dT%H:%M:%SZ
}

json_epoch_ms() {
    date -u +%s%3N
}
