# syntax=docker/dockerfile:1
#
# The base worker container image.
#
# One round of work happens in exactly one container built from this image, and
# nothing survives it. The image therefore carries everything a round needs and
# nothing that would need a port or a host mount to use:
#
#   * no EXPOSE, no port publishing, no listening service started at any point
#   * no bind mount, and no path outside the image is assumed to exist
#   * no credential of any kind baked in
#   * a non-root default user
#
# ADR-0010 puts the round's structured result in a single file the host lifts out
# with `docker cp`. ADR-0011 requires that file to be built by observing the
# container -- git for the files changed, the recording shell wrapper for the
# commands run, exit codes for the outcomes -- rather than by trusting anything
# the agent authored. ADR-0012 names the unprivileged user as a compensating
# control for running the factory on a rootful daemon.
#
# This is the *base* image. A project image adds that project's own toolchain on
# top; see docs/deriving-a-project-image.md.

ARG UBUNTU_VERSION=24.04

# ---------------------------------------------------------------------------
# Stage 1 - fetch the two self-contained binaries.
#
# Both are single archives with no system dependencies of their own, so they are
# fetched and verified in a throwaway stage that leaves no curl, no ca-certificates
# and no package cache behind in the final image.
# ---------------------------------------------------------------------------
FROM ubuntu:${UBUNTU_VERSION} AS fetch

# Pinned. Bump deliberately; the checksums move with the version.
ARG OPENCODE_VERSION=2.0.18
ARG OPENCODE_SHA256=62257e7b7f22083aede3e4c66b8057cecf0a7038846312b861afc9910e37935e
ARG CODE_SERVER_VERSION=4.139.1
ARG CODE_SERVER_SHA256=53029be6c5781b7bca49b815fcc9a2a3fc111813ad8c9965b2c0f0d2985a0674

SHELL ["/bin/bash", "-o", "pipefail", "-c"]

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl; \
    rm -rf /var/lib/apt/lists/*; \
    mkdir -p /dl; \
    curl -fsSL --retry 3 -o /dl/opencode.tar.gz \
      "https://opencode.ai/files/bin/${OPENCODE_VERSION}/opencode-linux-x64.tar.gz"; \
    echo "${OPENCODE_SHA256}  /dl/opencode.tar.gz" | sha256sum -c -; \
    curl -fsSL --retry 3 -o /dl/code-server.tar.gz \
      "https://github.com/coder/code-server/releases/download/v${CODE_SERVER_VERSION}/code-server-${CODE_SERVER_VERSION}-linux-amd64.tar.gz"; \
    echo "${CODE_SERVER_SHA256}  /dl/code-server.tar.gz" | sha256sum -c -; \
    mkdir -p /dl/opencode /dl/code-server; \
    tar -xzf /dl/opencode.tar.gz -C /dl/opencode; \
    tar -xzf /dl/code-server.tar.gz -C /dl/code-server --strip-components=1

# ---------------------------------------------------------------------------
# Stage 2 - the worker image itself.
# ---------------------------------------------------------------------------
FROM ubuntu:${UBUNTU_VERSION}

# Had no root certificates, so every HTTPS fetch in a round -- cloning the
# project's repository, restoring package caches -- failed with an opaque error.
SHELL ["/bin/bash", "-o", "pipefail", "-c"]

LABEL org.opencontainers.image.title="agent-factory base worker" \
      org.opencontainers.image.description="Disposable worker container: one round of work, no ports, no mounts, no root." \
      org.opencontainers.image.source="https://github.com/NaniSoft/agent-factory"

# Runtime essentials for a round, and nothing more.
#   git            ADR-0006: the round commits to a branch; the host pushes.
#   curl           the round fetches the repository and its own dependencies.
#   ca-certificates  TLS trust, for the same.
#   jq             small, and the image's own result file is JSON-lines.
#   ripgrep        an agent reading a code tree reaches for it constantly.
#   procps         ps/pgrep, for an agent diagnosing a build that hung.
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends \
      ca-certificates \
      curl \
      git \
      jq \
      procps \
      ripgrep; \
    rm -rf /var/lib/apt/lists/*; \
    \
    # The image runs as this user and nothing else. A home is required: OpenCode
    # writes its config, session data and logs under it, and a round that cannot
    # write there cannot start.
    #
    # The stock `ubuntu` account is removed rather than reused. It owns uid/gid
    # 1000, it is a member of the sudo group, and a worker container has no use
    # for either. Leaving it would mean the image ships an account nobody chose.
    userdel --remove ubuntu 2>/dev/null || true; \
    groupdel ubuntu 2>/dev/null || true; \
    groupadd --gid 1000 agent; \
    useradd --uid 1000 --gid 1000 --create-home --home-dir /home/agent \
            --shell /bin/bash agent; \
    \
    # git needs an identity to commit with (ADR-0006: the round commits locally
    # and never pushes), and needs to refuse dubious ownership rather than trust
    # a tree it did not create.
    git config --system user.name  "agent-factory worker"; \
    git config --system user.email "worker@agent-factory.invalid"; \
    git config --system init.defaultBranch main; \
    git config --system safe.directory '*'; \
    \
    # /work is the repository the round builds in. /out is the factory's own
    # directory and is deliberately *outside* /work, so the result, the command
    # log and the agent's note can never show up in the diff the reviewer judges.
    mkdir -p /work /out /opt/code-server /usr/local/lib/agent-factory; \
    chown -R agent:agent /work /out

# OpenCode: the agent. The standalone binary is self-contained, so the image
# needs no Node, no npm and no runtime to keep patched.
COPY --from=fetch /dl/opencode/opencode /usr/local/bin/opencode

# code-server: carried because the design names it as part of the editing
# environment. Deliberately extracted from the release tarball rather than
# installed from the .deb, because the package's post-install creates a system
# `coder` account and a systemd unit -- this image starts no service, ever, and
# carries no account that exists to start one. It is not started, not exposed,
# and not required by anything below. Publishing it is a deployment decision
# outside the scope of this build.
COPY --from=fetch /dl/code-server/ /opt/code-server/
RUN set -eux; \
    ln -s /opt/code-server/bin/code-server /usr/local/bin/code-server; \
    chmod 0755 /opt/code-server/bin/code-server; \
    opencode --version; \
    code-server --version; \
    # `--version` makes code-server write a default config under root's home.
    # Nothing in this image runs as root, so shipping that file would be both
    # dead weight and a hint that root is a place things happen.
    rm -rf /root/.config/code-server /root/.local/share/code-server

# The factory's own scripts: a recording shell wrapper, the collector that turns
# observations into the result file, and the round entrypoint.
COPY lib/json.sh        /usr/local/lib/agent-factory/json.sh
COPY bin/run            /usr/local/bin/run
COPY bin/worker-collect /usr/local/bin/worker-collect
COPY bin/worker-round   /usr/local/bin/worker-round
RUN set -eux; \
    chmod 0755 /usr/local/bin/run /usr/local/bin/worker-collect /usr/local/bin/worker-round; \
    chmod 0644 /usr/local/lib/agent-factory/json.sh; \
    chown -R root:root /usr/local/bin/run /usr/local/bin/worker-collect \
        /usr/local/bin/worker-round /usr/local/lib/agent-factory

# The contract the image speaks, resolved once so the .NET side and this image
# cannot drift on a path.
ENV AGENT_FACTORY_WORK=/work \
    AGENT_FACTORY_OUT=/out \
    AGENT_FACTORY_RESULT=/out/result.json \
    AGENT_FACTORY_COMMAND_LOG=/out/commands.ndjson \
    AGENT_FACTORY_NOTE=/out/note.md \
    AGENT_FACTORY_SCHEMA=agent-factory/worker-result@1

# No EXPOSE. That is the whole of the "publishes no inbound ports" claim on the
# image side; the host side is that the factory never passes -p or --publish.
WORKDIR /work
USER agent:agent

ENTRYPOINT ["/usr/local/bin/worker-round"]
CMD ["help"]
