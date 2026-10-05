# The worker container image

`worker/Dockerfile` builds `ghcr.io/nanisoft/agent-factory-worker:1`: the image a
[worker container](../CONTEXT.md) starts from, one per [round](../CONTEXT.md), with
nothing surviving it.

A **project file** names the image in its `worker.image` field. The running example
already says so:

```yaml
worker:
  image: ghcr.io/nanisoft/agent-factory-worker:1
```

## What the image is for, and what it refuses to be

A round happens in a container the factory does not trust, on a machine the factory
does not control, running code a model wrote. Every property below exists because of
that, and each one is a promise the image can be held to.

| Property | How it is enforced | Verified by |
| --- | --- | --- |
| Publishes no inbound port | No `EXPOSE`; nothing listens, ever | `docker image inspect` + `listenerCount` in the result |
| Mounts no host path | Nothing in the image needs one; the repository is fetched over the network | the test creates its container with no `-v` |
| Runs unprivileged | `USER agent:agent`, uid 1000, sole account in the image | `id` inside the round, recorded in the result |
| Carries no credential | none is baked in; the result records credential-shaped env var *names* so a reviewer can see what the agent was handed | `credentialEnvNames` in the result |
| Cannot write to a remote | the round commits and stops; there is nothing to push with | `remotes` in the result is empty |
| Says what it did rather than what it thinks | the result is built by observing, per ADR-0011 | the result file's own contents |

The evidence, rather than the claims:

```console
$ docker image inspect ghcr.io/nanisoft/agent-factory-worker:1 \
      --format 'User={{.Config.User}} ExposedPorts={{json .Config.ExposedPorts}} Volumes={{json .Config.Volumes}}'
User=agent:agent ExposedPorts=null Volumes=null

$ docker run --rm ghcr.io/nanisoft/agent-factory-worker:1 exec bash -c 'id; id -G; getent passwd | awk -F: "\$3>=1000"'
uid=1000(agent) gid=1000(agent) groups=1000(agent)
1000
agent:x:1000:1000::/home/agent:/bin/bash

$ docker run --rm ghcr.io/nanisoft/agent-factory-worker:1 exec \
      bash -c 'for f in /proc/net/tcp /proc/net/tcp6; do echo "$f: $(awk "NR>1 && \$4==\"0A\"" $f | wc -l) listening"; done'
/proc/net/tcp: 0 listening
/proc/net/tcp6: 0 listening
```

`groups=1000(agent)` with nothing appended is the part that matters: no supplementary
group, so no `docker`, no `sudo`, no `lxd`. Neither `sudo` nor `setcap` is installed, so
there is no in-container escalation path either. `Volumes=null` matters too — a `VOLUME`
instruction would have the daemon create an anonymous volume on every start, which is
exactly the kind of thing that accumulates across hundreds of rounds a day.

The unprivileged user is a named compensating control in [ADR-0012](../docs/adr/0012-rootless-is-a-deployment-property.md).
The development machine cannot satisfy the rootless requirement at all — Docker Desktop
reports `SecurityOptions: seccomp, cgroupns` with no rootless marker — and that
deviation is bounded by these properties rather than left open. See
[Deployment posture](#deployment-posture) for what this does and does not buy.

## Build and run it

```bash
docker build -t ghcr.io/nanisoft/agent-factory-worker:1 worker/
bash worker/smoke-test.sh            # build if needed, then prove it works
bash worker/smoke-test.sh --self-check   # prove the proof can fail
bash worker/offline-test.sh          # the scripts themselves, with no Docker at all
```

## What is inside

| | Version | Why |
| --- | --- | --- |
| `ubuntu` | 24.04 (glibc 2.39) | the widest set of toolchains a project image can add, with first-class apt sources for .NET, Node, Go and Rust |
| `opencode` | 2.0.18, standalone | the agent. One self-contained binary, so the image carries no Node runtime to keep patched |
| `code-server` | 4.139.1, standalone | named by the design as part of the editing environment. Carried, never started, never exposed |
| `git` | 2.43.0 | ADR-0006: the round commits to a branch; the host pushes |
| `curl`, `ca-certificates` | | the round fetches the repository and its own dependencies |
| `jq` | 1.7 | the result file is JSON lines, and something has to read it |
| `ripgrep`, `procps` | | an agent reading a tree and diagnosing a hung build reach for these constantly |

Both large binaries are downloaded and checksum-verified in a throwaway build stage, so
the final image carries no `curl`, no `ca-certificates` and no package cache from the
build. `code-server` is extracted from its release tarball rather than installed from the
`.deb`: the package's post-install creates a system `coder` account and a systemd unit,
and this image starts no service and has no use for an account whose purpose is starting
one.

## The round contract

The image's entrypoint is `/usr/local/bin/worker-round`. A round is one whole agent
session, and the container's lifetime is the round's boundary, so the entrypoint does the
collecting rather than leaving it to the host.

```
worker-round agent run "<issue text>"   # drive the OpenCode CLI non-interactively (ADR-0004)
worker-round exec  ./scripts/verify.sh # run something else
worker-round shell -c 'make test'
worker-round collect                    # write the result file from a round that already ended
```

ADR-0010 gives a round exactly two channels out. The result is **one file** the host
lifts with `docker cp`; the container log is the other and carries live progress.

```
AGENT_FACTORY_WORK=/work            the repository checkout
AGENT_FACTORY_OUT=/out             the factory's own directory, deliberately outside /work
AGENT_FACTORY_RESULT=/out/result.json
AGENT_FACTORY_COMMAND_LOG=/out/commands.ndjson
AGENT_FACTORY_NOTE=/out/note.md    the agent's one optional sentence
AGENT_FACTORY_REPO_URL             clone this into /work if it is empty
AGENT_FACTORY_BASE_REF             branch or ref to check out
```

`/out` is outside `/work` on purpose. A result written inside the working tree would show
up in the diff the reviewer is being asked to judge, and the entrypoint refuses to do it.

A round that should fetch its own tree:

```bash
docker run --rm \
  -e AGENT_FACTORY_REPO_URL=https://github.com/octocat/Hello-World.git \
  -e AGENT_FACTORY_BASE_REF=master \
  ghcr.io/nanisoft/agent-factory-worker:1 \
  agent run "say hello"
```

No `-p`, no `-v`. The factory must never pass either, and the smoke test asserts the
container it created has no mounts and no published ports.

## The result file

JSON **lines**: one self-contained object per line, so a truncated result costs the tail
and nothing else. Line 1 is always the header, which is how a round that produced nothing
else is still not invisible (story 29).

A real one, from the smoke test's round, trimmed:

```json
{"kind":"result","schema":"agent-factory/worker-result@1","roundExitCode":0,
 "work":"/work","out":"/out","gitRepo":true,
 "head":"ca910034359b32e74128e7303612aa8a845ce08b","branch":"main",
 "startHead":"b2e490bfea...","user":"agent","uid":1000,
 "credentialEnvNames":[],"git":"git version 2.43.0","opencode":"opencode v2.0.18",
 "codeServer":"4.139.1 53c2f325...","os":"Ubuntu 24.04.5 LTS","kernel":"Linux 6.11 ..."}

{"kind":"command","seq":1,"label":"the tests, after the change","argv":["./scripts/test.sh"],
 "cwd":"/work","env":[],"startedAt":"2026-09-28T11:33:50Z","durationMs":11,"exitCode":0,
 "captured":true,"stdoutBytes":25,"stdoutTail":"test: 1 passed, 0 failed\n","stderrTail":""}

{"kind":"git","field":"status","byteLength":87,"truncated":false,
 "text":"# branch.oid ca910034...\n# branch.head main\n? scratch.tmp\n"}

{"kind":"git","field":"diffFromRoundStart","byteLength":635,"truncated":false,
 "text":"diff --git a/src/index.js b/src/index.js\n@@ -1,11 +1 @@\n-...\n+module.exports = { answer: 42 };\n"}

{"kind":"note","field":"note","byteLength":66,"text":"Added the answer to src/index.js ..."}

{"kind":"end","collectedInMs":630,"commandRecords":8}
```

Record kinds: `result` (header), `command` (one per recorded invocation, verbatim from
the wrapper), `git` (raw observations, one record per `field`), `note` (the agent's
optional sentence), `listener` (a socket found listening — expect none), `end` (trailer).

**The image records; it does not interpret.** Nothing in the collector asks the agent for
anything, and nothing in it decides what a test failure means. Files changed come from
`git status` and `git diff`, commands run from the recording wrapper, outcomes from exit
codes, and the prose note is copied raw and never parsed. Reading these records into
"which files changed, which tests passed" is the factory's result deriver's job, and it
is free to reshape them. What the image guarantees is that everything in the file was
observed rather than asserted.

The exit code of `docker run` is about the container, not the round: `0` when a result
file was written, `70` when it was not. A round whose last command failed still exits `0`
with `roundExitCode: 3` inside the file. The factory classifies those differently — a
container that broke is transient and retryable, a round that failed is a result.

Two fields are worth knowing about:

- `diffFromRoundStart` is `git diff` against the commit the round *started* at, so it
  covers committed and uncommitted work alike. That is the diff the reviewer judges, and
  it is why the entrypoint captures the base before the agent touches anything.
- `status` catches untracked files, which `git diff` does not. The smoke test leaves a
  scratch file behind precisely so both paths are exercised.

## The recording shell wrapper

`/usr/local/bin/run` is how a command is recorded. It runs the command, records what it
ran, what came back and what it exited with, and **exits with the command's own exit
code** so a caller can never confuse a wrapper failure for a real one.

```sh
run [-C DIR] [-e KEY=VALUE]... [-o LABEL] [--] COMMAND [ARG...]

run pnpm test
run -o 'the unit tests' ./scripts/test.sh
```

Output is streamed, not buffered. ADR-0010 makes `docker logs` the channel carrying a
round's live progress, so a 90-minute round must not look silent until it ends. Each
stream is teed to the log and to a temporary file at once, and the file is trimmed to its
tail as soon as the command finishes (`AGENT_FACTORY_OUTPUT_TAIL_BYTES`, default 4000;
`AGENT_FACTORY_CAPTURE_OUTPUT=0` disables capture entirely and streams only).

Wrappers nest: a round that runs a script which itself calls `run` records both the
script and every command inside it, with the sequence number assigned when the record is
written so it increases down the file either way.

### What the wrapper does not see

This is the honest limit, and it is a real one.

`run` records what goes **through it**. A command the agent reaches by another route — a
tool that shells out on its own, a package manager's lifecycle script, a command typed
into a `bash` the agent opened itself — is not recorded, and the round's command list
under-reports it. The image cannot close this from the inside, because it does not know
which routes an agent will take.

Three things contain it in practice, and none is complete:

1. **Prompt the agent to use it.** `run <command>` reads naturally, and the round brief is
   the factory's to write.
2. **Point the agent's shell at it.** Setting `SHELL` to a wrapper that records and then
   `exec`s a real bash captures every command the agent's shell tool starts, because most
   such tools invoke `$SHELL -c "<command>"`. This is not enabled by default: it depends on
   OpenCode's shell integration behaving, and a silently-unrecorded shell is worse than a
   known-incomplete one.
3. **Read the diff, not the command list, for what actually shipped.** `diffFromRoundStart`
   and `status` are observed from git and do not depend on the wrapper at all. A build tool
   that bypassed `run` still shows up in the change.

Whoever wires the agent's configuration up should decide between 1 and 2 explicitly. It is
recorded here as a known gap rather than left to be discovered.

## The smoke test

```bash
bash worker/smoke-test.sh               # 58 checks
bash worker/smoke-test.sh --self-check  # proves the above can fail
```

It builds the image if it is not there, runs a real round in a container created with no
`-p` and no `-v`, lifts the result file out with `docker cp`, and checks the properties
above by reading them back off the built image and the finished container rather than off
the Dockerfile that claims them. It also lifts the round's tree out, because ADR-0006
depends on the host being able to reach the commit.

**It does not call a model.** An LLM is not what is under test, and a test that needs one
tests the API key. The round is a script that behaves the way an agent behaves: it runs
commands that pass and commands that fail, changes a file, commits, leaves a scratch file,
and writes a note.

`--self-check` runs the identical suite against a deliberately broken image that can run a
command and cannot return a result, and fails unless the suite goes red. It is there
because the round-output property is the one most likely to be quietly faked, and a test
that cannot fail is not a test. Currently 12 of 28 checks fail against the broken image.

The test's own fixture is itself a project image — the base plus a project, derived exactly
as [`docs/deriving-a-project-image.md`](docs/deriving-a-project-image.md) describes. The
repository is baked in rather than mounted because a worker container gets no host paths,
so the test would otherwise have no way to have a repository at all.

## The offline test

```bash
bash worker/offline-test.sh               # 129 checks
```

**This one needs no Docker.** It runs `bin/run`, `bin/worker-collect` and `bin/worker-round`
straight out of the repository, over real git repositories it builds itself, and reads back
what they wrote. `bash`, `git` and nothing else: no daemon, no image, no network, no model.

It exists because of #41. Between the image's first commit and that ticket, three of the four
files this `Dockerfile` copies were not in the repository — the wrapper, the collector and
the entrypoint, which between them are the whole of what the image does in a round. Nothing
caught it, and the reason is worth keeping: **an image built outside git makes its inputs
invisible to everything that reads the tree.** The image existed on the machine that had it,
so every question of the form "is the image there?" was answered yes. The one test that
would have caught it needs a daemon, and a machine with no daemon never ran it.

So the build's inputs got a check that does not need the daemon — both in C#
(`src/agent-factory.tests/Containers/WorkerImageInputsTests.cs`, which reads this
`Dockerfile`'s own `COPY` lines and asserts each source is in the tree, and asks `git
check-ignore` whether anything under `worker/bin/` is excluded again) and in the scripts
themselves, which is what this suite exercises.

What it can check is the half that goes wrong silently: the exit codes the wrapper returns,
what it records, in what order, with which numbers, whether a command's failure is kept
distinct from the round's own (#22), whether a credential name is recorded and its value is
not, and whether the host can read the result back. What it cannot check is what the
*image* installs — that these scripts are executable, that `opencode` and `code-server` are
present, that the container mounts no host path and publishes no port, that `docker cp`
lifts the result out. Those are properties of a built image. Both suites are needed; this
one is the one you can run anywhere.

## Deployment posture

**This image is not a sandbox.** It is a fresh disposable container, an unprivileged user,
no published ports, no host mounts, and a credential scoped to one round — the compensating
controls ADR-0012 lists. It is not rootless Docker, and it cannot be.

ADR-0012 requires a rootless daemon **where the factory runs for real**. On Docker Desktop
the daemon reports `SecurityOptions: seccomp, cgroupns` with no rootless marker, and there
is no rootless Docker Desktop to switch to. The development machine cannot satisfy the
requirement; claiming otherwise would be the dishonesty the Nexus docs explicitly guard
against. The properties above are what makes that deviation bounded rather than open-ended.

Two things follow, and neither is a detail:

- **On a deployment, the factory checks the daemon before it builds anything.** Running
  against a rootful daemon is out of policy. A development machine that loses any one of
  the compensating controls has stopped being a safe place to run the factory, whatever
  `docker info` reports.
- **`opencode` starts a background service that binds a loopback port inside the
  container.** Nothing publishes it, so it is unreachable from the host — but it is a
  listening socket, and the collector records any it finds rather than assuming there are
  none. Pass `--standalone` if a deployment wants the round to run with a private server
  that dies with the process.

## Known gaps

- **The command log under-reports** commands the agent runs without `run`. See
  [What the wrapper does not see](#what-the-wrapper-does-not-sees).
- **`code-server` costs a throwaway config file.** `code-server --version` writes a default
  config into the agent's home. It is the price of shipping a tool this image never
  starts, it lands in a container that is about to be deleted, and the collector filters
  code-server's log line out of the version it reports.
- **Only linux/amd64.** Both binaries are fetched for that platform and the checksums are
  pinned to it. A project needing arm64 has to build its own image; that is the recipe's
  job, not the base image's.
- **The result field set is this image's, not the factory's.** The result deriver owns the
  schema. If it wants a different shape it should reshape these records rather than the
  image inventing new ones, and `worker-collect` is the one place to change.
- **Retrieving the round's commit is not wired up here.** The image leaves the tree at
  `/work` on a branch with the commit on it, and `docker cp` lifts it — the smoke test
  proves both. Turning that into a push and a pull request is the merger's ticket
  (ADR-0006), not this one.
