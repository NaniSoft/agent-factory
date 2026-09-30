# The application container: one process serving the board and hosting every
# component (ADR-0003). It starts worker and workspace containers as *siblings*
# on the daemon the socket points at, so it needs the Docker CLI — the daemon
# itself stays on the host and is never inside this image.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ ./src/
RUN dotnet publish src/agent-factory -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# The Docker CLI, and nothing of the daemon: the factory talks to the host's
# daemon through the mounted socket and never runs one of its own. That the CLI
# comes from Docker's own repository rather than the distro's `docker.io` package
# is what keeps this image carrying no dockerd.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && install -m 0755 -d /etc/apt/keyrings \
    && curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian bookworm stable" \
        > /etc/apt/sources.list.d/docker.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends docker-ce-cli \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /publish ./

# The three things a deployment keeps: project files, credential values, and the
# store of record with the rounds' lifted trees beside it. Volumes, in the
# compose file — declared there so one `docker compose up` stands everything up.
ENV Factory__FactoriesDirectory=/app/factories \
    Factory__DatabasePath=/app/data/agent-factory.db \
    Factory__BoardUrl=http://0.0.0.0:5000

ENTRYPOINT ["dotnet", "agent-factory.dll"]
