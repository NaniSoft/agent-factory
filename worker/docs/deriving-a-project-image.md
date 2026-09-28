# Deriving a project image

The base worker image (`ghcr.io/nanisoft/agent-factory-worker:1`) carries the agent, git,
code-server, the recording wrapper and the round machinery. It deliberately carries **no
project's toolchain**, because it serves every project and no project in particular.

A **project image** is the base image plus the toolchain one project builds with, plus
optionally that project's own conventions. It is what a project file's `worker.image`
names, and it is derived, never hand-edited: the base image's properties — no ports, no
mounts, unprivileged user, no credentials — are inherited, not re-established.

The smoke test's own fixture image is a project image built this way
([`../smoke/Dockerfile`](../smoke/Dockerfile)), so the recipe below is exercised by the
test suite on every run rather than being a document nobody has ever followed.

## The shape of it

```dockerfile
# syntax=docker/dockerfile:1
ARG BASE_IMAGE=ghcr.io/nanisoft/agent-factory-worker:1
FROM ${BASE_IMAGE}

USER root
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends <the project's toolchain>; \
    rm -rf /var/lib/apt/lists/*
# ... and anything else: a language runtime, a version manager, a prebuilt binary.

USER agent:agent

# A command, not an ENTRYPOINT. The base image's entrypoint is what runs a round
# and writes the result file; replacing it would throw that away.
CMD ["agent", "run", "--"]   # or whatever the round's first step is
```

Build it:

```bash
docker build -t <registry>/<project>-worker:1 <context>/
```

## The four rules

### 1. `USER agent:agent`, at the end

The base image's `USER` is `agent:agent`. A project image that adds a toolchain needs
`USER root` to install it, so switch back explicitly before the end. A derived image that
forgets is an image that runs rounds as root, and nothing downstream would notice — the
round's own result would say `uid: 0` and be the only evidence.

```dockerfile
USER root
RUN ... install ...
USER agent:agent          # <- do not omit this
```

Anything the toolchain writes at build time must be readable by uid 1000, and anything it
writes at *run* time must land somewhere writable: `/work` and `/out` are the two
directories that are, and `$HOME` is the agent's.

`git` is configured to trust whatever tree it is pointed at (`safe.directory`), so a
`/work` the round does not own does not stop the round from *reading* it. It does not
extend to writing: a `/work` left owned by root gives `git status` exit 0 and then
`git commit` `fatal: Unable to create '/work/.git/index.lock': Permission denied`, and the
round looks like it is failing for no reason. The `chown -R agent:agent` in the example
below is not decoration.

### 2. No `EXPOSE`, and no port publishing anywhere

The design claims worker containers need no inbound ports. That is only true if nothing
in the image listens, so a project image must not add anything that starts a daemon —
no database server, no dev server, no `serve`, no language runtime with a hot-reload
watcher running by default.

If a project genuinely needs one for the agent to use during a round, the honest options
are both outside this image: a separate container the round talks to over the network, or
a process the round starts and stops inside its own lifetime. Shipping a listening
service in the worker image would break a property the factory's security posture rests on.

code-server is the standing precedent: it is in the image, it is on `PATH`, and it is never
started. Carrying a tool and starting it are different decisions.

### 3. No bind mounts, and nothing that assumes one

A worker container is given no host paths (ADR-0010), so a project image must not depend
on one. A build that reads `../something-outside-the-tree`, or a configuration file the
maintainer was going to mount in, fails at the first round rather than at build time.

The tree arrives by `git clone` inside the round, from `AGENT_FACTORY_REPO_URL`, and the
result leaves by `docker cp`. Those are the only two crossings.

### 4. No credentials, and nothing that looks like one

The round holds nothing that can write to a remote (ADR-0006). Do not `ARG` a token, do not
`COPY` a `.npmrc` or a `settings.xml` holding a password, do not bake a licence file for a
service the agent reaches over the network.

The collector records the *names* of credential-shaped environment variables in every
result (`credentialEnvNames`), so a reviewer can see what the agent was handed. A toolchain
that bakes a secret in produces a result that is silently wrong about that, which is why
this is worth checking rather than assuming.

## What to add, per ecosystem

The base is Ubuntu 24.04, so the upstream installation path for each of these is the one
that already works on a clean `noble`.

| The project is | Add | Watch out for |
| --- | --- | --- |
| .NET | `packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb`, then the SDK | the SDK wants a writable `$HOME/.dotnet`; it has one |
| Node | `apt install nodejs npm`, or a `nvm`/`volta` layer | `corepack enable` needs `USER root`; the agent's home is writable so the cache works |
| Python | `apt install python3 python3-pip python3-venv`; add a venv to `PATH` | system `pip install` needs `--break-system-packages` or a venv — prefer a venv in `/work` |
| Go | `apt install golang-go`, or the tarball to `/usr/local/go` | pin the version the project's `go.mod` asks for, not the distro's |
| Rust | `apt install cargo rustc`, or `rustup` to `/usr/local/rustup` | the agent's home is writable so the registry cache works |
| Java | a Temurin or `openjdk-*-jdk` package | pick a JDK the project's build actually pins |

Whatever the toolchain, **the version is the project's, not the image's.** A project image
that is one release behind a language the repository has already moved on produces rounds
that fail for reasons that have nothing to do with the issue being built.

For a language with a build cache, put it under `/work` or `$HOME` so it is discarded with
the container. A cache in an image layer is a cache that outlives the round, and a worker
container is supposed to leave nothing behind.

## Verifying a derived image

Do not take the derived image's word for anything. The fastest real check:

```bash
bash worker/smoke-test.sh --image <registry>/<project>-worker:1
```

That runs the whole suite against it: the unprivileged user, the absent ports, the absent
mounts, a real round, and the result file coming back out. An image that passes is a
project image; an image that does not is not, whatever its Dockerfile says.

Two things it will not tell you, which you have to check yourself:

- **The toolchain is actually usable**, at the version the project needs. Open a container
  and build something in it.
- **The toolchain does not start anything.** The suite checks that nothing is *listening*;
  a process that is running and harmless today is one config change away from a socket.

## Two worked examples

### A .NET project

```dockerfile
ARG BASE_IMAGE=ghcr.io/nanisoft/agent-factory-worker:1
FROM ${BASE_IMAGE}

USER root
ARG DOTNET_VERSION=10.0
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl; \
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh; \
    bash /tmp/dotnet-install.sh --channel "${DOTNET_VERSION}" --install-dir /usr/local/share/dotnet; \
    rm -f /tmp/dotnet-install.sh; \
    ln -s /usr/local/share/dotnet/dotnet /usr/local/bin/dotnet; \
    rm -rf /var/lib/apt/lists/*
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    PATH="/usr/local/share/dotnet:${PATH}"

USER agent:agent
CMD ["agent", "run", "--"]
```

No `EXPOSE`: Kestrel is a library the project's own test command starts, in the round, for
as long as the test takes. Nothing in the image serves anything.

### A project image that also bakes in its repository

Worth it only for a project whose tree is large or whose dependencies are slow to fetch,
and never as a way around the "no host path" rule — it is still derived, still fetched at
build time, still gone when the container goes.

```dockerfile
ARG BASE_IMAGE=ghcr.io/nanisoft/agent-factory-worker:1
FROM ${BASE_IMAGE}

USER root
ARG REPO_URL
ARG BASE_REF=main
RUN set -eux; \
    apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*; \
    git clone --branch "${BASE_REF}" --single-branch "${REPO_URL}" /work; \
    chown -R agent:agent /work; \
    git -C /work config --system safe.directory '*' 2>/dev/null || true
USER agent:agent
```

The trade is real and so is the upside: the round starts without a network fetch. The costs
are that the image is now tied to one commit — set `BASE_REF` deliberately and re-tag on
every move — and that the clone happens as root, so the tree's ownership has to be fixed
explicitly, as above. When the tree is not large, prefer `AGENT_FACTORY_REPO_URL` and let
the round fetch it: the round then records *how* the tree got there, in the command log,
and the image stays reusable across commits.

Note that `REPO_URL` and `BASE_REF` are build arguments. Passing a token as one of them
would bake a credential into an image layer, which is exactly what rule 4 forbids; use an
unauthenticated clone of a public repository, or leave the fetch to the round.
