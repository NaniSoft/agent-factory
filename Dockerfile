# ---- the React app ------------------------------------------------------------
# The UI is a static Next export. It is built first so the .NET publish below
# can copy the finished export into the assembly's wwwroot.
FROM node:22-bookworm-slim AS client
WORKDIR /client
RUN corepack enable
COPY src/AgentFactory.App/ClientApp/package.json src/AgentFactory.App/ClientApp/pnpm-lock.yaml src/AgentFactory.App/ClientApp/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/AgentFactory.App/ClientApp/ ./
RUN pnpm build

# ---- build (SDK; Ubuntu 24.04 is the .NET 10 default distro — no Debian tags exist) ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ ./src/
COPY --from=client /client/out ./src/AgentFactory.App/ClientApp/out/
RUN dotnet publish src/AgentFactory.App -c Release -o /app

# ---- runtime (ASP.NET Core runtime; chiseled rejected — this app shells out to CLIs) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG OPENCODE_VERSION=1.18.31
ARG GH_VERSION=2.103.3

# CLI tools the fleet shells out to (glibc/Ubuntu first-party channels)
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*

# gh: first-party release tarball (asset names carry the version without the v;
# the archive nests the binary at gh_<v>_linux_amd64/bin/gh)
RUN curl -fsSL "https://github.com/cli/cli/releases/download/v${GH_VERSION}/gh_${GH_VERSION}_linux_amd64.tar.gz" \
  | tar -xz -C /usr/local/bin --strip-components=2 "gh_${GH_VERSION}_linux_amd64/bin/gh" \
  && chmod 755 /usr/local/bin/gh

# opencode: pinned V1 release tarball — the same asset the official installer
# fetches (the ghcr image tops out at 1.0.196 and cannot carry this pin)
RUN curl -fsSL "https://github.com/anomalyco/opencode/releases/download/v${OPENCODE_VERSION}/opencode-linux-x64.tar.gz" \
  | tar -xz -C /usr/local/bin opencode \
  && chmod 755 /usr/local/bin/opencode

# git: fleet identity + gh credential helper + defensive safe.directory, all at
# SYSTEM scope (/etc/gitconfig) so every user and worktree inherits them — bare
# `git push` authenticates from GH_TOKEN through the helper (x-access-token).
RUN git config --system user.name  "Agent Factory Fleet" \
  && git config --system user.email "fleet@agent-factory.invalid" \
  && git config --system credential.https://github.com.helper '!/usr/local/bin/gh auth git-credential' \
  && git config --system --add safe.directory /data

WORKDIR /app
COPY --from=build /app .

# Fleet skill library (the machine's ~/.agents/skills as-is IS the library, and
# opencode discovers it from the user-global agentskills dir). Baked as a
# build-time snapshot. Named context keeps the machine-local library out of the
# build context:
#   docker build --build-context skills="$HOME/.agents/skills" -t agent-factory:dev .
COPY --from=skills . /home/app/.agents/skills/

# Target toolchains: every enrolled target's definition-of-done runs inside the
# container as the agent's final phase step — `dotnet build && dotnet test` for
# the C# targets, `node --test` for the JS ones. The runtime image ships no SDK
# and no node, so both layer in from their official images.
COPY --from=mcr.microsoft.com/dotnet/sdk:10.0 /usr/share/dotnet /usr/share/dotnet
COPY --from=node:22-bookworm /usr/local/bin/node /usr/local/bin/node
COPY --from=build /root/.nuget /home/app/.nuget

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    ASPNETCORE_HTTP_PORTS=8080

# Durable-state root (PVC-mounted at deploy; plain dirs for image smoke runs)
# and the app user's HOME — opencode's xdg roots hang off HOME and it boot-mkdirs
# its subdirs, but /home/app itself must exist and be writable. The chown also
# takes the skill library.
RUN mkdir -p /data/clones /data/worktrees /home/app \
  && chown -R "$APP_UID" /data /home/app

ENV HOME=/home/app \
    OPENCODE_DISABLE_AUTOUPDATE=true

# UI port; opencode serve stays loopback-only on Fleet__ServerPort in-container
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AgentFactory.App.dll"]
