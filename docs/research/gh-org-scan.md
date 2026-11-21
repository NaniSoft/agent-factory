# Research: gh CLI org/repo enumeration and `.github` repo mechanics

Ticket: [010-research-gh-org-scan](../../issues/010-research-gh-org-scan.md).
Environment verified: `gh` 2.97.0 on GitHub.com, authenticated as **DurgaPrasadReddyV**
(token scopes observed via `gh auth status`: `admin:org`, `gist`, `repo`, `workflow` —
an OAuth `gho_` token, i.e. already **broader** than "repo-scoped").
All probes were read-only against public orgs (`cli`, `google`, `microsoft`).

Command shapes below extend the existing `GitHubService` patterns
(`src/AgentFactory.App/Fleet/GitHubService.cs`): `Proc.RunAsync` for JSON lists,
`Proc.RunStdinAsync` + `--body-file -` for bodies, `-R OWNER/REPO` per call.

---

## 1. Org repo enumeration

### Recommended shape

```
gh repo list <org> --limit 3000 --json name,nameWithOwner,isArchived,isFork,hasIssuesEnabled,isPrivate,visibility,updatedAt,url
```

Verified probe (public org `cli`, 2026-10-24) — exact field shapes returned:

```json
[{"hasIssuesEnabled":true,"isArchived":false,"isEmpty":false,"isFork":false,
  "isInOrganization":true,"isPrivate":false,"name":"cli","nameWithOwner":"cli/cli",
  "updatedAt":"2026-10-24T06:00:32Z","visibility":"PUBLIC"}, ...]
```

Available `--json` fields (from `gh repo list --help`): `name`, `nameWithOwner`,
`isArchived`, `archivedAt`, `isFork`, `hasIssuesEnabled`, `isPrivate`, `visibility`,
`isEmpty`, `isInOrganization`, `updatedAt`, `createdAt`, `pushedAt`, `url`, `owner`,
`defaultBranchRef`, `viewerPermission`, `openGraphImageUrl`, `isUserConfigurationRepository`, …

Filters: `--no-archived` (omit archived), `--archived`, `--source` (non-forks),
`--fork`, `--visibility {public|private|internal}`, `--language`, `--topic`.

For enrollment the host wants non-archived, issues-enabled, non-fork sources —
either filter server-side (`--no-archived --source`) or client-side off the JSON.

### Limit and pagination behavior (probed, not guessed)

- `--limit` default is **30**. `gh repo list` auto-paginates 100-per-request until
  the limit is met.
- **No client-side 1000 cap**: `--limit 5001` was accepted without error, and
  `gh repo list google --limit 1200 --json name -q 'length'` returned exactly
  `1200` (12 underlying requests). The 1000 cap exists only on `gh search *` commands.
- **Practical ceiling** = the primary rate limit (5,000 req/hr, see §2), not the
  flag. A 6,000-repo org is ~60 requests per full re-list.
- **No sort flag; ordering is not contractual.** Observed order was not monotonic
  in `updatedAt` (cli 10-24, go-gh 10-22, oauth 10-23, …). Treat the result as an
  unordered set and diff snapshots by `nameWithOwner` — never by position.

### Raw REST variant (when one process call should fetch the whole org)

```
gh api --paginate --slurp "orgs/<org>/repos?per_page=100&type=all" -q '[.[][] | {name: .name, full_name, archived, fork, has_issues, visibility, updated_at}]'
```

Field names differ from `--json` (REST: `archived`, `fork`, `has_issues`,
`updated_at`, `full_name` — verified via probe). `--paginate` follows `Link`
headers; `--slurp` wraps pages into one JSON array for a single deserialize.

**Gotcha (Windows, Git Bash only)**: `gh api /rate_limit` fails with
`invalid API endpoint: "C:/Program Files/Git/rate_limit"` — MSYS rewrites leading
slash. Use `gh api rate_limit` (no leading slash) when testing from Git Bash;
the C# host (`Process.Start`) is unaffected.

## 2. Issue scan at scale

### Rate limits (verified via `gh api rate_limit`)

- **core (REST): 5,000/hr** — `{"limit":5000,"remaining":4999,...}`.
- **search: 30/min** — the tight budget that rules the design.
- **graphql: 5,000/hr**.

Docs: <https://docs.github.com/en/rest/rate-limit/rate-limits-about>,
<https://docs.github.com/en/rest/search/search?apiVersion=2022-11-28> (search:
30 req/min authenticated; 1,000-result hard cap per query; 100/page).

### Per-repo listing (recommended backbone)

```
gh issue list -R <owner>/<repo> --state open --limit 1000 --json number,title,body,url,createdAt,updatedAt,labels
```

- `--limit` default 30; **no client-side cap** (probed: `--limit 1001` accepted).
  Underlying REST `repos/{o}/{r}/issues` serves 100/page, gh auto-paginates.
- Ordering: default is created-desc (newest first) on the REST endpoint
  (`sort=created&direction=desc` verified). For **oldest-first backlogs**:
  - `gh issue list -R <r> --search "sort:created-asc" ...` (documented in the
    command's own examples), or
  - raw: `gh api "repos/<o>/<r>/issues?state=open&sort=created&direction=asc&per_page=100&page=1"`.
- `--json` fields incl. `number,title,body,url,createdAt,updatedAt,labels,state,author,comments,milestone`.

### Org-wide search (fallback index, not the backbone)

```
gh search issues --owner <org> --state open --sort created --order asc --limit 1000 --json repository,number,title,createdAt,url
```

Verified: works, returns `repository.nameWithOwner` per row, oldest-first across
the whole org. But:

- `--limit` hard-capped at **1000** (probed: `--limit 1001` →
  `` `--limit` must be between 1 and 1000 ``).
- Search bucket is **30 req/min** — one org-wide query per 2s max, shared with
  anything else using search.
- The search **index is not real-time** (can lag behind issue creation), and is
  not guaranteed to cover every repo configuration. Use it for "first enrollment
  overview" or one-shot discovery, not for the poll loop.

### First-enrollment slicing recommendation

Per-repo `gh issue list` (or REST `sort=created&direction=asc`) is the reliable
path: deterministic order, fresh data, core bucket (5,000/hr). Suggested policy:
enrollment snapshot takes **oldest-first up to a cap per repo** (e.g. `--limit
200` via `--search "sort:created-asc"`), records "capped" state, and pages deeper
(`page=N`) only on demand while the backlog drains. Reserve `gh search issues
--owner <org>` for a single org-wide overview call at enrollment time.

## 3. The org `.github` repo

### GitHub's conventions (cited)

- It is the **default community health files** repo: `CONTRIBUTING.md`,
  `CODE_OF_CONDUCT.md`, `SECURITY.md`, `SUPPORT.md`, `ACCESSIBILITY.md`,
  `ISSUE_TEMPLATE/`, `DISCUSSION_TEMPLATE/`, `FUNDING.yml` placed in its
  `.github/` folder or root apply org-wide to repos that lack their own copy.
  Docs: <https://docs.github.com/en/communities/setting-up-your-project-for-healthy-contributions/creating-a-default-community-health-file>
- **Not auto-created** — someone makes it like any repo.
- For the *health-files* role it **must be public** ("A repository for default
  files cannot be private"); the defaults then apply to private repos in the org
  too. A private `.github` still works fine as a plain repo (e.g. as the routing
  inbox) but serves no org-wide defaults.
- **Issues filed into it are not special-cased** — it is an ordinary repo with
  the issues feature (verified: `microsoft/.github` has `hasIssuesEnabled: true`).
  Using it as the cross-repo routing intake is a convention Agent Factory imposes,
  not a GitHub behavior; nothing breaks, nothing auto-forwards.

### Detection (verified gotcha)

`isUserConfigurationRepository` sounds perfect but is **false for an org's
`.github`** (verified via GraphQL on `microsoft/.github`:
`isUserConfigurationRepository: false` — it means the *personal* configuration
repo). Detect by name:

```
gh repo list <org> --limit 3000 --json name,url -q ".[] | select(.name == \".github\")"
# or one lookup:
gh api repos/<org>/.github -q .full_name   # 404 = absent
```

### Creation if absent

```
gh repo create <org>/.github --public --add-readme --disable-wiki --description "Agent Factory cross-repo routing intake and org-wide community files"
```

(`--private` also exists if the org wants a private intake and forfeits the
health-file defaults; `--internal` on Enterprise.)

**Prerequisites** (docs: <https://docs.github.com/en/rest/repos/repos#create-an-organization-repository>
and <https://docs.github.com/en/organizations/managing-organization-settings/restricting-repository-creation-in-your-organization>):

- The authenticated user must be a **member of the org**; members additionally
  need the org setting "Members can create repositories" to allow that
  visibility (org owners can always create; owners configure the setting under
  Settings → Member privileges).
- Classic/OAuth token: `public_repo` or `repo` scope for public, `repo` for
  private. **The current token already has full `repo` (plus `admin:org`) — no
  scope elevation needed for any operation in this doc.**
- Post-create, label provisioning uses the existing `EnsureOptInLabelExists`
  pattern with `-R <org>/.github`.

## 4. Cross-referencing routed issues

All of it is plain `gh issue` calls with `-R`, extending `GitHubService`:

```
# 1. File the routing issue in .github (body via stdin, --body-file -)
gh issue create -R <org>/.github --title <quoted> --body-file - --label <optInLabel>

# 2. Comment on the original with the back-reference (stdin body)
gh issue comment <N> -R <owner>/<repo> --body-file -

# 3. Close the original as duplicate, pointing at the routing issue — one call:
gh issue close <N> -R <owner>/<repo> --reason duplicate --duplicate-of <org>/.github#<M> --comment <quoted>
```

Verified flags on gh 2.97.0: `issue close` has `-c/--comment`,
`--duplicate-of <number-or-URL>`, `-r/--reason {completed|not planned|duplicate}`.

- **Cross-repo references beyond markdown links**: writing `owner/repo#123`
  anywhere on GitHub (or the full URL) autolinks and — unlike a bare markdown
  link — also generates a **backlink (cross-referenced event)** in the
  referenced issue's timeline, including across repos. Prefixing with
  `redirect.github.com` suppresses the backlink when unwanted.
  Docs: <https://docs.github.com/en/get-started/writing-on-github/working-with-advanced-formatting/autolinked-references-and-urls>.
  There is **no REST/GraphQL mutation that creates a formal cross-repo issue
  relation** (the web UI's relate/duplicate machinery is not exposed); a formal
  sub-issue hierarchy exists in the API but gh 2.97.0 has no subcommand for it —
  validate before relying on it. The `owner/repo#N` backlink is the mechanism.
- **Alternative: transfer instead of route** — `gh issue transfer <N> <org>/.github`
  (gh 2.97.0 has it). Moves the issue itself: comments/assignees kept, **labels
  and milestones dropped**, requires write access to **both** repos, private→public
  transfers blocked, old URL 301-redirects.
  Docs: <https://docs.github.com/en/issues/tracking-your-work-with-issues/administering-issues/transferring-an-issue-to-another-repository>.
  Comment+close-as-duplicate keeps the original context (labels, repo history)
  and is reversible; transfer is cleaner but lossy — decision belongs to ticket 014.

## 5. Watch semantics (poll cadence)

Cited base: <https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api>
— conditional requests with `If-None-Match` return **304 and do not count
against the primary rate limit** (when authorized); keep request parameters
byte-identical across polls (different params → different ETags); prefer webhooks
over polling; respect `retry-after` / `x-ratelimit-reset`; ~1s between mutations
(secondary rate limits).

### New issues on an enrolled repo (the cheap path)

```
gh api -i "repos/<o>/<r>/issues?state=open&sort=created&direction=desc&per_page=100&since=<lastPollIso8601>" \
  -H "If-None-Match: <savedEtag>"
```

- Verified: `gh api -i` prints the status line + headers, and a `304 Not
  Modified` **exits 0 with an empty body** — the host just checks the first
  output line for `304` (and captures the returned `ETag` for next poll).
  Verified live against `repos/cli/cli`.
- `since` filters by **updated_at** (verified), so a hit includes
  comment-activity false positives — filter client-side on `created_at > watermark`.
- New-issue detection therefore costs **0 quota when nothing changed**.

### New repos in an enrolled org

No `since` on `orgs/{org}/repos`, and ETag-on-page-1 is unreliable (ordering
isn't created-desc, §1) — so **re-list and diff**:

```
gh repo list <org> --limit 3000 --json nameWithOwner,isArchived,updatedAt,hasIssuesEnabled
```

One request per 100 repos against the 5,000/hr core bucket; diff `nameWithOwner`
against the stored snapshot. Pair it with conditional GETs on unchanged repos'
issue lists (above) and the whole watch loop is nearly free. `gh api --cache 60m`
also exists for simple TTL caching.

### Suggested cadence

- Per-repo new-issue poll: conditional GET every cycle (304s are free).
- Org re-list: every N cycles (e.g. 10–15 min) — cheap and order-independent.
- Mutations (create/comment/close): serial, ≥1s apart.

## Scope / token gaps

- **No elevation needed with the current token**: `repo` (all repos incl. private)
  covers enumeration, issue scans, `.github` creation (given org membership +
  member-create rights), comments, closes, transfers. `admin:org` is *more* than
  anything here requires.
- Hygiene note for later: the `gho_` token carries `admin:org, workflow, gist` —
  broader than Agent Factory needs. If it's ever swapped for a **fine-grained PAT**:
  org-wide private repo enumeration requires the token to be granted
  all-repositories access *within that org* (subject to the org's API authorization
  policy), issue work needs `Issues: read/write`, and org repo creation needs org
  admin-grants — flagging now because fine-grained + orgs is policy-gated, unlike
  the current classic-scope token.
- Search API is out as a poll backbone: 30/min, 1000-result cap, index lag.
- Open item for ticket 014: whether routing = comment+close-as-duplicate (keeps
  context) vs `gh issue transfer` (clean, lossy). Both are ready to wire.

## Sources

- gh manual / local: `gh repo list --help`, `gh issue list --help`,
  `gh issue close --help`, `gh issue transfer --help`, `gh search issues --help`,
  `gh api --help` (gh 2.97.0, 2026-10-24).
- Default community health files (`.github` repo):
  <https://docs.github.com/en/communities/setting-up-your-project-for-healthy-contributions/creating-a-default-community-health-file>
- Create an organization repository (permissions):
  <https://docs.github.com/en/rest/repos/repos#create-an-organization-repository>
- Restricting repository creation in an organization:
  <https://docs.github.com/en/organizations/managing-organization-settings/restricting-repository-creation-in-your-organization>
- Repository roles for an organization:
  <https://docs.github.com/en/organizations/managing-user-access-to-your-organizations-repositories/repository-roles-for-an-organization>
- REST best practices (conditional requests, ETag/304 not rate-limited, polling):
  <https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api>
- Search API (30/min, 1000 cap):
  <https://docs.github.com/en/rest/search/search?apiVersion=2022-11-28>
- Autolinked references and URLs (cross-repo `owner/repo#N`, backlinks):
  <https://docs.github.com/en/get-started/writing-on-github/working-with-advanced-formatting/autolinked-references-and-urls>
- Transferring an issue to another repository:
  <https://docs.github.com/en/issues/tracking-your-work-with-issues/administering-issues/transferring-an-issue-to-another-repository>
- List repository issues (sort/direction/since):
  <https://docs.github.com/en/rest/issues/issues#list-repository-issues>
