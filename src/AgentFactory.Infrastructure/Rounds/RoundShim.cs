namespace AgentFactory.Infrastructure.Rounds;

using System.Text;

/// <summary>
/// The recording command shim, written into every round's landing directory and put at the
/// front of the round's <c>PATH</c>. This is the host-side successor of the worker image's
/// <c>bin/run</c> wrapper, and it keeps the same contract: it runs a command, records what
/// it ran, what came back and what it exited with, and <strong>exits with the command's own
/// exit code</strong> so a caller can never mistake a shim failure for a real one.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0011 makes this the only account of what a round ran that the factory believes, and
/// the same honesty the image's README stated carries over unchanged: it under-reports,
/// because the brief can only <em>ask</em> the agent to prefix its shell work with
/// <c>run</c> — the agent's own shell tool is not pointed at it. A command that bypasses
/// the shim is still visible in the files git reports. The guarantee is carried by
/// <c>git status</c> and <c>git diff</c>, never by the command log.
/// </para>
/// <para>
/// Three files, because a round's agent may reach the shim through any of the three shells
/// it is likely to have: <c>run</c> (a bash script, for the shell OpenCode prefers when
/// there is a POSIX one), <c>run.ps1</c> (PowerShell, where the recording is easiest), and
/// <c>run.cmd</c>, which is the batch shim that calls into PowerShell. All three write the
/// same JSON-lines log — <see cref="RoundCollector.CommandLogFileName"/> — in the same
/// record shape the image's wrapper wrote, because the collector and the deriver read both
/// and the two cannot be allowed to disagree about what a command record says.
/// </para>
/// <para>
/// The script and the library it sources are LF-only whatever line endings the checkout
/// used: a CRLF in a shell script is a carriage return in every argument on it. The
/// normalisation lives here, at the definition.
/// </para>
/// </remarks>
public static class RoundShim
{
    /// <summary>The sequence file, beside the log: wrappers nest, and the number is assigned at write time.</summary>
    public const string SequenceFileName = ".run-seq";

    /// <summary>Writes the shim into the landing directory. Cheap, and idempotent per attempt.</summary>
    public static void WriteInto(string landing)
    {
        File.WriteAllText(Path.Combine(landing, "run"), RunScript.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(landing, "json.sh"), JsonLibrary.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(landing, "run.ps1"), PowerShellScript.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(landing, "run.cmd"), BatchScript.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
    }

    /// <summary>
    /// The bash shim: the image's <c>bin/run</c>, with the process substitution traded for
    /// capture-to-file-then-replay so it runs in any shell the agent points at it.
    /// </summary>
    private const string RunScript = """
        #!/usr/bin/env bash
        #
        # The round's recording shim, as the brief describes it: it runs a command, records
        # what it ran, what came back and what it exited with, and exits with the command's
        # own exit code.
        #
        #   run [-C DIR] [-e KEY=VALUE]... [-o LABEL] [--] COMMAND [ARG...]
        #
        # It under-reports on purpose and says so: a command the agent reaches without this
        # prefix is not recorded, and the files git reports are the guarantee, not this log.

        set -uo pipefail

        . "$(dirname "$0")/json.sh"

        label=''
        dir=''
        envs=()

        while [ $# -gt 0 ]; do
            case "$1" in
                -C) [ $# -ge 2 ] || { printf 'run: -C needs a directory\n' >&2; exit 64; }
                    dir="$2"; shift 2 ;;
                -e) [ $# -ge 2 ] || { printf 'run: -e needs KEY=VALUE\n' >&2; exit 64; }
                    envs+=("$2"); shift 2 ;;
                -o) [ $# -ge 2 ] || { printf 'run: -o needs a label\n' >&2; exit 64; }
                    label="$2"; shift 2 ;;
                --) shift; break ;;
                -*) printf 'run: unknown option %s\n' "$1" >&2; exit 64 ;;
                *)  break ;;
            esac
        done

        if [ $# -eq 0 ]; then
            printf 'run: no command given\n' >&2
            exit 64
        fi

        out="${AGENT_FACTORY_OUT:-.}"
        log="${AGENT_FACTORY_COMMAND_LOG:-$out/commands.ndjson}"

        recording=1
        if ! mkdir -p "$(dirname "$log")" 2>/dev/null || ! : >> "$log" 2>/dev/null; then
            printf 'run: cannot write the command log at %s, so this command will run unrecorded\n' "$log" >&2
            recording=0
        fi

        seqfile="$out/.run-seq"

        capture=1
        [ "${AGENT_FACTORY_CAPTURE_OUTPUT:-1}" = '0' ] && capture=0

        tail_bytes="${AGENT_FACTORY_OUTPUT_TAIL_BYTES:-4000}"

        stdout_file=''
        stderr_file=''
        if [ "$capture" = 1 ]; then
            stdout_file="$(mktemp "${TMPDIR:-/tmp}/run-out.XXXXXX")" || capture=0
            if [ "$capture" = 1 ]; then
                stderr_file="$(mktemp "${TMPDIR:-/tmp}/run-err.XXXXXX")" || capture=0
            fi
        fi

        started="$(json_now)"
        start_ms="$(json_epoch_ms)"

        launcher=(env)
        for addition in "${envs[@]:-}"; do
            [ -n "$addition" ] && launcher+=("$addition")
        done

        code=0
        if [ -n "$dir" ]; then
            if ! cd "$dir" 2>/dev/null; then
                printf 'run: cannot enter %s\n' "$dir" >&2
                code=66
            fi
        fi

        if [ "$code" -eq 0 ]; then
            if [ "$capture" = 1 ]; then
                "${launcher[@]}" "$@" > "$stdout_file" 2> "$stderr_file"
                code=$?
            else
                "${launcher[@]}" "$@"
                code=$?
            fi
        fi

        # The command's own streams, replayed after the fact. A shim that swallowed its
        # output would hide the work from the agent that asked for it; a shim that streamed
        # and captured at once needs a shell feature the capture above deliberately avoids.
        [ "$capture" = 1 ] && cat "$stdout_file" && cat "$stderr_file" >&2

        ended_ms="$(json_epoch_ms)"
        duration=$(( ended_ms - start_ms ))

        if [ "$recording" = 1 ]; then
            seq=0
            if [ -r "$seqfile" ]; then
                read -r seq < "$seqfile" 2>/dev/null || seq=0
                case "$seq" in ''|*[!0-9]*) seq=0 ;; esac
            fi
            seq=$(( seq + 1 ))
            printf '%s\n' "$seq" > "$seqfile" 2>/dev/null

            stdout_bytes=0
            stderr_bytes=0
            stdout_tail=''
            stderr_tail=''
            if [ "$capture" = 1 ]; then
                stdout_bytes=$(wc -c < "$stdout_file" 2>/dev/null | tr -d ' ') || stdout_bytes=0
                stderr_bytes=$(wc -c < "$stderr_file" 2>/dev/null | tr -d ' ') || stderr_bytes=0
                stdout_tail=$(tail -c "$tail_bytes" "$stdout_file" 2>/dev/null)
                stderr_tail=$(tail -c "$tail_bytes" "$stderr_file" 2>/dev/null)
            fi

            {
                printf '{"kind":"command","seq":%d,"label":%s,"argv":%s,"cwd":%s,"env":%s,"startedAt":%s,' \
                    "$seq" "$(json_lit "$label")" "$(json_string_array "$@")" \
                    "$(json_lit "$PWD")" "$(json_string_array "${envs[@]:-}")" "$(json_lit "$started")"
                printf '"durationMs":%d,"exitCode":%d,"captured":%s,"stdoutBytes":%d,"stderrBytes":%d,' \
                    "$duration" "$code" "$([ "$capture" = 1 ] && printf true || printf false)" \
                    "${stdout_bytes:-0}" "${stderr_bytes:-0}"
                printf '"stdoutTail":%s,"stderrTail":%s}\n' \
                    "$(printf '%s' "$stdout_tail" | json_string)" \
                    "$(printf '%s' "$stderr_tail" | json_string)"
            } >> "$log" 2>/dev/null
        fi

        [ -n "$stdout_file" ] && rm -f "$stdout_file"
        [ -n "$stderr_file" ] && rm -f "$stderr_file"

        # The command's own exit code, always.
        exit "$code"
        """;

    /// <summary>The JSON helpers the bash shim sources — the image's own library, unchanged.</summary>
    private const string JsonLibrary = """
        # JSON emission helpers for the round's recording shim.
        #
        # There is no jq dependency here: `run` writes one record per command, potentially
        # thousands of times, and shelling out to jq each time is wasted work. These are
        # sed and tr, which are already present.
        #
        # The command log is JSON *lines*: one self-contained object per line. That is what
        # makes a damaged log degrade to a partial one — the collector reads it line by
        # line, and a truncated tail costs the tail and nothing else.

        json_escape() {
            LC_ALL=C tr -d '\000-\010\013\014\016-\037' \
            | LC_ALL=C sed -z \
                -e 's/\\/\\\\/g' \
                -e 's/"/\\"/g' \
                -e 's/\t/\\t/g' \
                -e 's/\r/\\r/g' \
                -e 's/\n/\\n/g'
        }

        json_string() {
            printf '"%s"' "$(json_escape)"
        }

        json_lit() {
            printf '%s' "$1" | json_string
        }

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

        json_now() {
            date -u +%Y-%m-%dT%H:%M:%SZ
        }

        json_epoch_ms() {
            date -u +%s%3N
        }
        """;

    /// <summary>
    /// The PowerShell shim: the same record, the same log, the same exit-code contract, in
    /// the shell a Windows round's agent most likely reaches for.
    /// </summary>
    private const string PowerShellScript = """
        # The round's recording shim, PowerShell half. Same contract as the bash shim beside
        # it: run the command, record it, exit with its exit code. Under-reports the same
        # way, for the same reason, with git carrying the guarantee.

        $ErrorActionPreference = 'Continue'

        $label = ''
        $dir = ''
        $envs = @()
        $rest = @()

        $argsOnly = $args
        $i = 0
        while ($i -lt $argsOnly.Count) {
            switch ($argsOnly[$i]) {
                '-C' { $dir = $argsOnly[$i + 1]; $i += 2; continue }
                '-e' { $envs += $argsOnly[$i + 1]; $i += 2; continue }
                '-o' { $label = $argsOnly[$i + 1]; $i += 2; continue }
                '--' { $i += 1; break }
                default { break }
            }
            $i++
        }
        for (; $i -lt $argsOnly.Count; $i++) { $rest += $argsOnly[$i] }

        if ($rest.Count -eq 0) {
            [Console]::Error.WriteLine('run: no command given')
            exit 64
        }

        $out = if ($env:AGENT_FACTORY_OUT) { $env:AGENT_FACTORY_OUT } else { '.' }
        $log = if ($env:AGENT_FACTORY_COMMAND_LOG) { $env:AGENT_FACTORY_COMMAND_LOG } else { Join-Path $out 'commands.ndjson' }

        $recording = $true
        try {
            $logDir = Split-Path -Parent $log
            if ($logDir -and -not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
            if (-not (Test-Path $log)) { Add-Content -Path $log -Value '' -Encoding UTF8 | Out-Null }
            if (-not (Test-Path $log)) { throw 'unwritable' }
        } catch {
            [Console]::Error.WriteLine("run: cannot write the command log at $log, so this command will run unrecorded")
            $recording = $false
        }

        $seqFile = Join-Path $out '.run-seq'

        $tailBytes = 4000

        $stdoutFile = [IO.Path]::GetTempFileName()
        $stderrFile = [IO.Path]::GetTempFileName()

        $started = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        $clock = [Diagnostics.Stopwatch]::StartNew()

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $rest[0]
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.RedirectStandardInput = $true
        foreach ($argument in $rest[1..($rest.Count - 1)]) { [void]$psi.ArgumentList.Add($argument) }
        foreach ($addition in $envs) {
            $split = $addition.IndexOf('=')
            if ($split -gt 0) { $psi.Environment[$addition.Substring(0, $split)] = $addition.Substring($split + 1) }
        }
        if ($dir) { $psi.WorkingDirectory = $dir }

        $code = 0
        try {
            $process = [System.Diagnostics.Process]::Start($psi)
            try { $process.StandardInput.Close() } catch [System.IO.IOException] { }
            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEnd()
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $process.WaitForExit()
            $code = $process.ExitCode
            [IO.File]::WriteAllText($stdoutFile, $stdout)
            [IO.File]::WriteAllText($stderrFile, $stderr)
            Write-Output $stdout
            [Console]::Error.Write($stderr)
        } catch {
            [Console]::Error.WriteLine("run: could not start $($rest[0]): $_")
            $code = 66
        }

        $duration = [long]$clock.Elapsed.TotalMilliseconds

        if ($recording) {
            $seq = 0
            if (Test-Path $seqFile) {
                [void][int]::TryParse((Get-Content $seqFile -ErrorAction SilentlyContinue), [ref]$seq)
            }
            $seq += 1
            Set-Content -Path $seqFile -Value $seq

            $stdoutText = [IO.File]::ReadAllText($stdoutFile)
            $stderrText = [IO.File]::ReadAllText($stderrFile)
            $stdoutBytes = [Text.Encoding]::UTF8.GetByteCount($stdoutText)
            $stderrBytes = [Text.Encoding]::UTF8.GetByteCount($stderrText)

            function Tail([string]$text, [int]$bytes) {
                $encoded = [Text.Encoding]::UTF8
                $total = $encoded.GetByteCount($text)
                if ($total -le $bytes) { return $text }
                $index = 0
                while ($index -lt $text.Length -and $total -gt $bytes) {
                    $total -= $encoded.GetByteCount($text, $index, 1)
                    $index++
                }
                return $text.Substring($index)
            }

            $record = [ordered]@{
                kind = 'command'
                seq = $seq
                label = $label
                argv = $rest
                cwd = (Get-Location).Path
                env = $envs
                startedAt = $started
                durationMs = $duration
                exitCode = $code
                captured = $true
                stdoutBytes = $stdoutBytes
                stderrBytes = $stderrBytes
                stdoutTail = (Tail $stdoutText $tailBytes)
                stderrTail = (Tail $stderrText $tailBytes)
            }

            $line = ($record.GetEnumerator() | ForEach-Object {
                $value = $_.Value
                if ($value -is [array]) {
                    $inner = ($value | ForEach-Object { '"' + ($_ -replace '\\', '\\\\' -replace '"', '\"') + '"' }) -join ','
                    "[$inner]"
                } elseif ($value -is [bool]) {
                    if ($value) { 'true' } else { 'false' }
                } elseif ($value -is [int] -or $value -is [long]) {
                    "$value"
                } else {
                    '"' + ("$value" -replace '\\', '\\\\' -replace '"', '\"' -replace "`r", '\r' -replace "`n", '\n' -replace "`t", '\t') + '"'
                }
            }) -join ','

            Add-Content -Path $log -Value "{$line}" -Encoding UTF8
        }

        Remove-Item $stdoutFile, $stderrFile -ErrorAction SilentlyContinue

        exit $code
        """;

    /// <summary>The batch half: a shim named <c>run</c> a Windows shell can resolve, answering to PowerShell.</summary>
    private const string BatchScript = """
        @echo off
        powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
        """;
}
