# Research: container toolchain for the all-in-one image

Ticket: [036-research-container-toolchain](../../issues/036-research-container-toolchain.md)
Date: 2026-10-30. The app under containerization is the .NET 10 Blazor
Server app (`net10.0`) that shells out to `git`, `gh`, and `opencode`
([repo] `src/AgentFactory.App/Program.cs`, `Fleet/GitService.cs`,
`Fleet/GitHubService.cs`, `Fleet/OpenCodeEngine.cs`). Sources are marked:

- **[docs]** — official documentation or announcements (linked per claim).
- **[source:v1.18.31]** — github.com/anomalyco/opencode at tag `v1.18.31`,
  file and line cited.
- **[source:cli/cli]** — github.com/cli/cli source at `trunk`.
- **[registry]** — direct registry observation (npm, ghcr) this session.
- **[repo]** — in-repo code observation this session.
- **[community]** — issue trackers/forums where **no official statement
  exists**; explicitly not authoritative, flagged for live verification.

Facts current as of 2026-10-30.

---

## 1. .NET 10 base images: which, and why

**Debian is off the table for .NET 10.** With .NET 10 the default Linux
distro for container tags changed from Debian to Ubuntu 24.04
("noble"), and maintainers confirmed "We will not be publishing .NET 10
images with Debian" [docs,
github.com/dotnet/dotnet-docker/discussions/6801, announced 2025-11-13].
Tag families that exist: `10.0` / `10.0-noble` (Ubuntu), `10.0-noble-chiseled`
(distroless), `10.0-alpine` / `10.0-alpine3.22` (musl),
`10.0-azurelinux3.0` (+ `-distroless`) [docs, same discussion].

- **Runtime choice: `mcr.microsoft.com/dotnet/aspnet:10.0`** — the app is
  Blazor Server (interactive server render mode), so it needs the
  ASP.NET Core runtime, i.e. the `aspnet` repo, not `runtime` [docs,
  same discussion lists the repos; aspnet = "ASP.NET Core and .NET
  runtimes"]. The build stage uses `sdk:10.0` in a multi-stage
  Dockerfile; the SDK never ships in the final image.
- **Chiseled is rejected for this app.** Chiseled/distroless = "no
  shell", "no package manager", non-root by default [docs, dotnet-docker
  README distroless section]. Agent Factory *shells out* to git/gh/opencode
  constantly and installs them via apt/tarballs at build time — no shell
  means no apt and no easy git (git is not shippable as one static
  file), so chiseled fights the app's whole execution model.
- **Alpine is viable but divergent.** `aspnet:10.0-alpine3.22` works for
  .NET (musl), and opencode 1.18.31 *does* ship musl builds
  (`opencode-linux-x64-musl.tar.gz` [registry, release assets, §2]). But
  musl forces the musl opencode build and the community gh package
  route; Ubuntu (glibc) matches what the host runs and lets git and gh
  come from first-party channels. Ubuntu wins on least surprise.
- **Non-root**: since .NET 8, the Linux images define a non-root user
  `app` (UID **1654**, exposed as the `$APP_UID` env var); the
  recommended pattern is a one-line `USER $APP_UID` [docs,
  devblogs.microsoft.com/dotnet/announcing-dotnet-8-container-images,
  devblogs.microsoft.com/dotnet/securing-containers-with-rootless]. This
  works on the plain Ubuntu-based `aspnet:10.0` image — no extra
  useradd needed.

## 2. opencode CLI: pinning 1.18.31 in the image

**Context gotcha first**: opencode now has a V2 line. The official docs
warn Homebrew's default formula and Arch's package install **V2**, while
the `anomalyco/tap` Homebrew tap and the `opencode-ai` npm package track
**V1** — and "OpenCode 1 and 2 both use the `opencode` command and can't
coexist side by side by default" [docs, opencode.ai/docs]. The fleet's
whole contract (001/027) is V1 1.18.31 — every install path below must
pin explicitly.

Verified pin-capable install paths for V1 1.18.31 on linux/amd64:

1. **Official install script, pinned** (recommended for parity with the
   host): the script documents `-v, --version <version>` and the usage
   example `curl -fsSL https://opencode.ai/install | bash -s --
   --version 1.0.180` [source:v1.18.31 install script,
   opencode.ai/install]. It refuses V2 releases (>= 2.0.0), resolves the
   right asset per OS/arch/libc (detects musl via `/etc/alpine-release`
   or `ldd`), downloads from
   `github.com/anomalyco/opencode/releases/download/v<ver>/<asset>`, and
   installs to **`$HOME/.opencode/bin/opencode`**.
   Container note: run it with `HOME` pointed where the binary should
   land (or at the build user, then copy/move to `/usr/local/bin`) — a
   root build would otherwise hide it in `/root/.opencode/bin`.
2. **Release tarball directly** (leanest, what the script itself does):
   `https://github.com/anomalyco/opencode/releases/download/v1.18.31/opencode-linux-x64.tar.gz`
   contains a top-level `opencode` binary (the installer extracts and
   `mv`s `tmp_dir/opencode`) [source:v1.18.31 install script]. Assets
   confirmed for v1.18.31: `opencode-linux-x64.tar.gz` (glibc),
   `opencode-linux-x64-musl.tar.gz`, `-baseline` (no-AVX2) variants,
   arm64 equivalents, desktop bundles [registry, GitHub release assets].
   No published checksum file was verified this session — pin by digest
   of the tarball if reproducibility matters.
3. **npm**: `opencode-ai@1.18.31` exists [registry, registry.npmjs.org],
   is a tiny wrapper (unpacked ~7.9 KB) whose platform binary comes via
   optionalDependencies (`opencode-linux-x64@1.18.31`,
   `opencode-linux-x64-musl@1.18.31`, …) and a `postinstall` script. It
   requires a Node runtime in the image and adds a postinstall
   supply-chain step — heavier than 1/2 for a .NET image.

**The official container image is NOT an option for the pin**:
`ghcr.io/anomalyco/opencode` exists but its tag list tops out at
**1.0.196** — no 1.18.31 tag [registry, ghcr tags/list, 2026-10-30].

**Data/config homes under a non-root user** [source:v1.18.31
`packages/core/src/global.ts` — `xdg-basedir`]:
`$XDG_DATA_HOME/opencode` else `~/.local/share/opencode` (holds
`auth.json` — the `PUT /auth` target of 027 — plus `log/`, `repos/`),
`$XDG_CONFIG_HOME/opencode` else `~/.config/opencode` (global
`opencode.json` [docs, opencode.ai/docs/config]), `~/.cache/opencode`
(`bin/` — the autoupdate install target), `~/.local/state/opencode`,
`/tmp/opencode`. All of these are `mkdir -p`'d at boot
[global.ts]. `OPENCODE_CONFIG_DIR` / `OPENCODE_CONFIG` /
`OPENCODE_CONFIG_CONTENT` override the config dir/file/content
[source:v1.18.31 `packages/core/src/flag/flag.ts`].

**serve in a container** [docs, opencode.ai/docs/server]:
`opencode serve [--port] [--hostname]` — defaults port 4096, hostname
**127.0.0.1**; auth via `OPENCODE_SERVER_PASSWORD` (HTTP basic, username
defaults `opencode`, `OPENCODE_SERVER_USERNAME` overrides). This matches
the engine's spawn exactly — `serve --port {ServerPort} --hostname
127.0.0.1 --print-logs --log-level INFO` + generated password [repo,
`Fleet/OpenCodeEngine.cs`]. In-pod, loopback binding is correct: the
Blazor app talks to the serve from inside the same container; nothing
else needs to reach it. The docs contain **no Docker-specific
guidance** — container-specific serve failure modes are unverified and
get proven live at 039 (map's prove-it bar).

## 3. gh CLI: install, GH_TOKEN semantics, scopes

**Install in the image** [docs,
github.com/cli/cli/blob/trunk/docs/install_linux.md]: two first-party
routes — the official apt repo (`cli.github.com/packages`, keyring
fingerprint `2C6106201985B60E6C7AC87323F3D4EA75716059`) on Ubuntu/Debian
bases, or the precompiled release archives (Go binaries for amd64/arm64)
from cli/cli releases. Community routes are explicitly unsupported:
Debian/Ubuntu community packages were flagged broken in 2025, Alpine's
`apk add github-cli` is community-maintained, and "never installing gh
as a snap" is the team's own advice. For a pinned image, the release
tarball is the deterministic choice.

**GH_TOKEN env auth** [docs, cli.github.com/manual/gh_help_environment]:
`GH_TOKEN` (or `GITHUB_TOKEN`) is "an authentication token that will be
used when a command targets either github.com or a subdomain of ghe.com"
and "Setting this avoids being prompted to authenticate and **takes
precedence over previously stored credentials**". The login docs call
env-token auth "most suitable for 'headless' use of gh such as in
automation" [docs, cli.github.com/manual/gh_auth_login]. This is exactly
the container replacement for the host's keyring-based login (map
inventory): the container has no interactive keyring, so env is the
native mechanism, matching charting decision 4.

**Scopes needed** [docs, cli.github.com/manual/gh_auth_login]:

- gh's own minimum for `--with-token`: **`repo`, `read:org`, `gist`**.
  Agent Factory needs the first two: `repo` (or `public_repo` on a classic
  token for a public playground) covers HTTPS clone/push and PR
  creation; `read:org` covers `gh repo list <org>` / org existence checks
  (OrgScanner, GitHubService) [repo]. `gist` is not used by the app.
- Fine-grained alternative (docs advise "Favour setting GH_TOKEN for
  fine-grained personal access token usage"): Contents read/write
  (clone/push), Pull requests read/write, Issues read/write (intake +
  comments), Metadata read (implicit).

**Making bare `git push` work (the piece 039 must not miss)**: the app
pushes with plain `git push -u origin <branch>` [repo,
`Fleet/GitService.cs`] over HTTPS — no token in the URL. gh bridges this:
`gh auth setup-git` configures git to use gh as a credential helper
[docs, cli.github.com/manual/gh_auth_setup-git]; in source it writes
`credential.<host>.helper = !<gh-path> auth git-credential` into the
**global** config (`git config --global`) [source:cli/cli
`pkg/cmd/auth/shared/gitcredentials/helper_config.go`]. The helper's
`get` operation resolves the active token for the host and serves it to
git — including env tokens: when the source ends in `_TOKEN` it
 authenticates git as username `x-access-token` with the token as
password [source:cli/cli `pkg/cmd/auth/gitcredential/helper.go`]. So
`GH_TOKEN` in env + the helper configured ⇒ clone and push work without
any stored credential.

Two deployment nuances: (a) `setup-git` writes *global* config, i.e. the
`$HOME/.gitconfig` of whoever runs it — run it as the runtime user at
container start, or (simpler, deterministic) write the same helper line
into **system** config (`/etc/gitconfig`) at build time; (b) the manual
says setup-git errors when no hosts are authenticated (with `-f
--hostname` as the override) — in a GH_TOKEN-only container the
system-gitconfig route sidesteps that ambiguity entirely. Recommended:
system gitconfig in the Dockerfile.

## 4. git in-container, non-root

- **Identity**: both the agents (commit inside worktrees, ADR 0001) and
  the host (GitService's "untrack" commit) run `git commit` as the
  container user; without `user.name`/`user.email` configured, commit
  fails. Set it once at **system scope** in the image
  (`git config --system user.name/user.email` → `/etc/gitconfig`) so it
  applies to every user and every worktree; env
  `GIT_AUTHOR_*`/`GIT_COMMITTER_*` would also work but are easier to
  lose between processes. Pick a fleet identity (e.g. `Agent Factory
  Fleet` / an address on the org's domain) — 039's call.
- **safe.directory**: git's ownership check ("detected dubious ownership
  in repository") triggers when the repository is owned by a different
  user than the one executing git, and `safe.directory` is only
  respected in **protected configuration** (system, global, or command
  scope) — never in the repo's own config [docs,
  git-scm.com/docs/git-config, Protected configuration + safe.directory].
  The desired posture needs **no safe.directory entries at all**: if the
  same non-root UID both creates the clones/worktrees (the app does,
  via `git clone`/`git worktree add`) and later reads them, ownership
  matches by construction. Add one defensive system-scope line
  (`git config --system --add safe.directory /data`) to cover the
  cross-uid edge (a root-run init step, or `kubectl exec` as a different
  user) — harmless when unneeded.
- **Worktree durability**: worktrees live under the PVC-mounted
  worktree root and outlive runs; same UID every run (baked into the
  image) keeps file modes consistent. `git worktree remove --force` +
  `worktree prune` self-healing (GitService) is uid-agnostic once
  ownership matches.

## 5. `Fleet__CloneRoot` env-var override — verified for THIS app

Verified in code [repo]: `Program.cs` uses plain
`WebApplication.CreateBuilder(args)` and `Configure<FleetOptions>(
builder.Configuration.GetSection(FleetOptions.SectionName))`; a grep
across `src/` finds **no** `AddJsonFile`/`AddEnvironmentVariables`/
custom `IConfigurationSource` — the app runs on the pure default
provider chain. On that chain, the default app configuration sources
rank (highest→lowest): command line → environment variables (unprefixed)
→ user secrets (Development) → `appsettings.{Environment}.json` →
`appsettings.json`, and "key values read from the environment override
values read from `appsettings.json`" [docs,
learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration,
"Default app configuration sources" + "Environment Variables
Configuration Provider"]. The `__` delimiter is "supported by all
platforms and is automatically converted into a colon (`:`)"
[same doc]. `Configure<T>(section)` binds from the composed
configuration at first `IOptions` resolution, so
`Fleet__CloneRoot=/data/clones` (and `Fleet__DataDir=/data`,
`Fleet__WorktreeRoot=/data/worktrees`, …) binds over `appsettings.json`
for `FleetOptions` **as-is — no code change needed**.

Two consequences for 039/040:

- The override is **mandatory, not optional**: the shipped
  `appsettings.json` carries Windows absolute paths (`C:\\…` for
  CloneRoot/WorktreeRoot/DataDir) [repo], which are unusable on Linux.
  The ConfigMap must set every path-ish Fleet key (CloneRoot,
  WorktreeRoot, DataDir — plus PlaygroundRepo/BaseBranch etc. as
  desired); the code's `Path.IsPathRooted` check then uses `/data/...`
  as-is [repo, Program.cs RunStore/ConfigStore singletons].
- The app registers the options as a singleton `.Value` snapshot
  [repo, Program.cs] and `appsettings.json` reload-on-change won't
  touch it — config changes are pod-restart events, which matches the
  ConfigMap model (remount + restart).

## 6. Non-root uid vs the /data volume on Docker Desktop

- **Where the PVC actually lives**: the docker-desktop cluster's default
  StorageClass (`docker-desktop`, provisioner `docker.io/hostpath`)
  provisions PV directories **inside the Docker Desktop VM** (the WSL2
  backend distro), not on the Windows host [docs/community,
  docker/for-win#7023]. Windows-side paths are irrelevant; `hostPath`
  in a manifest would also refer to VM-internal paths.
- **The classic failure**: hostPath-provisioned PV directories are
  created by root (typically `root:root`, non-group-writable), so a pod
  running as a non-root UID gets `PermissionDenied` writing to the mount
  [community, minikube#1990 is the canonical writeup; k8s
  discuss "PV/hostpath and non-root owner"]. This will hit UID 1654 on
  the first 040 deploy unless something establishes ownership first.
- **Fixes, in preference order for this map**:
  1. **Root initContainer chown** (deterministic): a tiny initContainer
     (same image, `securityContext.runAsUser: 0`) runs
     `mkdir -p /data/{clones,worktrees}` + `chown -R 1654:0 /data` +
     group-writable chmod, then exits; the app container runs
     `USER $APP_UID`. Single-node dev cluster — the cost is nil.
  2. **`podSecurityContext.fsGroup`** — the Kubernetes-native mechanism
     (kubelet recursively chowns/chmods supported volumes to the fsGroup)
     [docs, k8s security-context docs], but fsGroup application on
     hostPath-backed volumes is **community-reported as inconsistent
     across versions** [community, kubernetes/kubernetes#45053 lineage
     and threads] — try it, verify live, keep (1) as the fallback.
- **Unverified — say-so**: whether fsGroup works on this exact
  docker-desktop build could not be established from official sources;
  it is a live-verification item for 040 (one Deployment with fsGroup vs
  the initContainer, first mount proves it).
- Persistence properties worth noting: the PVC outlives pods, so the
  chown runs again harmlessly on every pod start (idempotent); the app
  UID is stable across image rebuilds (1654 is baked into the base
  image's contract [docs, .NET 8 images announcement]), so ownership
  never silently shifts.

## 7. Recommended Dockerfile skeleton

Skeleton, not a tested Dockerfile — 039 verifies each step live. Every
constant shown is sourced above.

```dockerfile
# ---- build (SDK; Ubuntu 24.04 is the .NET 10 default distro) ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AgentFactory.App -c Release -o /app

# ---- runtime (ASP.NET Core runtime; Debian is NOT shipped for .NET 10) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG OPENCODE_VERSION=1.18.31
ARG GH_VERSION=<pin a current gh release at build time>

# CLI tools the fleet shells out to (glibc/Ubuntu channels)
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*

# gh: first-party release tarball (Go static binary; layout per its archive)
RUN curl -fsSL "https://github.com/cli/cli/releases/download/v${GH_VERSION}/gh_${GH_VERSION}_linux_amd64.tar.gz" \
  | tar -xz -C /usr/local/bin --strip-components=1 "gh_${GH_VERSION}_linux_amd64/bin/gh"

# opencode: pinned V1 release tarball — same asset the official installer fetches
RUN curl -fsSL "https://github.com/anomalyco/opencode/releases/download/v${OPENCODE_VERSION}/opencode-linux-x64.tar.gz" \
  | tar -xz -C /usr/local/bin opencode \
 && chmod 755 /usr/local/bin/opencode

# git: fleet identity + gh credential helper + defensive safe.directory,
# all at SYSTEM scope (/etc/gitconfig) so every user and worktree inherits them
RUN git config --system user.name  "Agent Factory Fleet" \
 && git config --system user.email "fleet@agent-factory.invalid" \
 && git config --system credential.https://github.com.helper '!/usr/local/bin/gh auth git-credential' \
 && git config --system --add safe.directory /data

WORKDIR /app
COPY --from=build /app .

# opencode needs HOME (xdg roots hang off it); keep the pinned binary pinned
ENV HOME=/home/app \
    OPENCODE_DISABLE_AUTOUPDATE=true

# UI port (image default ASPNETCORE_HTTP_PORTS=8080); opencode serve stays on 127.0.0.1:48180
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AgentFactory.App.dll"]
```

Secrets never enter the image: `GH_TOKEN` and the provider keys arrive
via pod env from the Secret (map decisions 3/4); the Dockerfile has no
credential surface.

## 8. Gotchas list (the short version)

1. **No Debian for .NET 10** — `10.0-bookworm-slim`-style tags don't
   exist; default is Ubuntu 24.04. Any blog recipe pinning Debian tags
   is pre-.NET-10.
2. **Chiseled/distroless is wrong for this app** — no shell/apt while
   the app's whole job is shelling out.
3. **opencode V1 vs V2** — untagged installs drift to V2 (brew/pacman);
   pin 1.18.31 via `--version` or the release tarball. The official
   ghcr image stops at 1.0.196 — unusable for the pin.
4. **`HOME` must be set for opencode** — its data/config/cache/state
   roots hang off `xdg-basedir`; no HOME = crash at boot. Data lands in
   `~/.local/share/opencode` (auth.json written by `PUT /auth` —
   container-ephemeral, by design: keys are injected per serve boot).
5. **Set `OPENCODE_DISABLE_AUTOUPDATE=true`** — autoupdate would replace
   the pinned binary at runtime (writes into `~/.cache/opencode/bin`).
6. **Bare `git push` needs the gh credential helper** — configure
   `credential.https://github.com.helper` at system scope in the image;
   `GH_TOKEN` alone changes nothing for git. gh env-token auth serves
   git pushes as user `x-access-token` (cli/cli source).
7. **`gh auth setup-git` writes global (per-user) config and errors with
   no authenticated hosts** — prefer the system-gitconfig line at build
   time over running setup-git in a GH_TOKEN-only container.
8. **git identity must be system-scoped in the image** — agents commit
   inside worktrees (ADR 0001); missing identity fails every run's
   commit. safe.directory should be unnecessary (same-uid posture) —
   add the /data line defensively at system scope only.
9. **`Fleet__*` env overrides work for this app (verified) but are
   mandatory** — shipped appsettings.json holds Windows absolute paths;
   ConfigMap must override CloneRoot/WorktreeRoot/DataDir at minimum.
   Options bind once (singleton snapshot) — config changes are restarts.
10. **First /data mount will be root-owned** — docker-desktop's hostPath
    PVs are created by root inside the VM; use a root initContainer
    chown to UID 1654 (or try fsGroup and verify live).
11. **`Fleet.ServerPort` must stay loopback-only** — the engine binds
    opencode serve to 127.0.0.1 [repo]; correct in-pod. Only the Blazor
    UI (8080) is exposed to the Service. `UseHttpsRedirection` with no
    HTTPS endpoint logs a warning and serves HTTP — expected in-pod.
12. **App-code blocker for 039 (repo-observed)**: `ToolLocator.Resolve`
    (ProcessTools.cs) finds tools via `where.exe` under
    `Environment.SystemDirectory` — Windows-only. On Linux the lookup
    fails and every git/gh/opencode resolution throws. 039 must extend
    ToolLocator (PATH-aware lookup or absolute Linux paths) before the
    container can run at all.

## Evidence index

- **[docs] .NET 10 container images**:
  github.com/dotnet/dotnet-docker/discussions/6801 (Ubuntu default, no
  Debian, tag families, 2025-11-13);
  devblogs.microsoft.com/dotnet/announcing-dotnet-8-container-images and
  devblogs.microsoft.com/dotnet/securing-containers-with-rootless
  (non-root `app` user, UID 1654, `$APP_UID`, `USER $APP_UID` pattern);
  devblogs.microsoft.com/dotnet/updates-to-dotnet-docker-images-in-net-8
  (ASPNETCORE_HTTP_PORTS=8080 default).
- **[docs] opencode**: opencode.ai/docs (install routes, V1/V2 split),
  opencode.ai/docs/config (config home), opencode.ai/docs/server (serve
  flags, OPENCODE_SERVER_PASSWORD), opencode.ai/install (script, fetched
  2026-10-30: `--version`, musl detection, `$HOME/.opencode/bin`,
  V2 refusal).
- **[source:v1.18.31]** anomalyco/opencode:
  `packages/core/src/global.ts` (xdg-basedir roots, boot mkdirs),
  `packages/core/src/flag/flag.ts` (OPENCODE_* env incl.
  `OPENCODE_SERVER_PASSWORD`, `OPENCODE_DISABLE_AUTOUPDATE`),
  `packages/opencode/src/auth/index.ts` (`Global.Path.data/auth.json`,
  `OPENCODE_AUTH_CONTENT`), release assets list for v1.18.31 (glibc +
  musl + baseline tarballs).
- **[source:cli/cli]** github.com/cli/cli at `trunk`:
  `docs/install_linux.md` (apt repo, release archives, community
  warnings), `pkg/cmd/auth/shared/gitcredentials/helper_config.go`
  (setup-git writes `--global` helper), `pkg/cmd/auth/gitcredential/
  helper.go` (helper serves env tokens, `x-access-token`).
- **[docs] gh manual**: cli.github.com/manual/gh_help_environment
  (GH_TOKEN precedence), /gh_auth_login (min scopes repo, read:org,
  gist; headless env auth; fine-grained advice), /gh_auth_setup-git
  (credential helper for authenticated hosts).
- **[docs] git**: git-scm.com/docs/git-config (protected configuration;
  safe.directory).
- **[docs] .NET configuration**:
  learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/
  (default source order; env overrides appsettings.json; `__` → `:`).
- **[registry]** registry.npmjs.org `opencode-ai@1.18.31` (wrapper +
  optionalDependencies + postinstall); ghcr.io/v2/anomalyco/opencode
  tags/list (max 1.0.196).
- **[community]** docker/for-win#7023 (PVs inside the Docker Desktop VM),
  minikube#1990 + kubernetes discuss "PV/hostpath and non-root owner"
  (root-owned provisioned dirs), kubernetes/kubernetes#45053 lineage
  (fsGroup on local/hostPath volumes inconsistent).
- **[repo]** this session: src/AgentFactory.App/Program.cs,
  appsettings.json, Fleet/FleetOptions.cs, Fleet/ProcessTools.cs
  (ToolLocator), Fleet/GitService.cs, Fleet/GitHubService.cs,
  Fleet/OpenCodeEngine.cs, docs/adr/0001.
