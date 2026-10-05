# Projects

**A project file in this directory is served.** The set of projects the factory serves is
exactly the `.yaml` files here — no registry, no allowlist, no second place to keep in step
(#1 story 8, story 5).

Which means this repository commits **no project file**, and the reason is #23 rather than
tidiness. A committed file here is not a configuration sample; it is a repository the factory
will poll, build issues from, and open and merge pull requests into, given a credential. The
only thing standing between a fresh checkout and a write to somebody's repository is that the
credential's environment variable happens to be unset — which is luck, not policy, and it is
true of every checkout.

So the examples live one directory over, in [`worker/examples/`](../worker/examples), where
they are readable and not loaded:

- [`nexus.project.yaml`](../worker/examples/nexus.project.yaml) — the repository the design
  is written about.
- [`example.project.yaml`](../worker/examples/example.project.yaml) — the same shape with
  placeholder values.

## Adding a project

Copy one of those here and fill in your own values, or use the board's
[projects page](http://127.0.0.1:5000/Projects), which writes the file for you.

| Field | |
| --- | --- |
| `name` | the project's name, unique across this directory |
| `repo.url` | absolute `http` or `https` URL |
| `worker.image` | the image each round's worker container starts from |
| `llm.model` | a full `provider/model` reference (#38) |
| `keys.github` | the **name** of an environment variable, never its value |
| `keys.llm` | the **name** of an environment variable, never its value |

A field beyond those six is **refused**, not ignored. So are shared, partial, included and
generated files: a project is always exactly one complete file someone wrote. A file that
fails validation keeps its own project out of the rotation and is reported on the board under
"Project files refused" — the factory still starts.

Configuration loads at start and does not hot reload, so **a changed file means a restart**.

## A repository with no commits

Not buildable, deliberately, and it fails visibly rather than obscurely (#24).

A round clones the repository at the work item's base and diffs against the commit it started
from. A repository with no commits has no such commit, and `git clone --branch main` against
one fails with `Remote branch main not found` and a non-zero exit — which the round reads as a
permanent failure, so the work item parks rather than retrying.

**The first commit is a human's.** The factory will not make one: it holds a write credential
for the *host*, and ADR-0006's whole shape is that the host pushes a commit a round made
inside a container that could not push anything. Inventing a project's first commit is a
different act from answering its first issue, and the design does not grant it.

So a greenfield project's owner pushes a README — or a `.gitignore`, or a LICENSE — before
adding the project file. One commit, from them, and every issue after it is the factory's.

## What a credential here can reach

The `keys.github` name is read on the **host**, by the merger, after the round's container is
gone (ADR-0006). It is never passed into a worker container, so a confused, injected or
compromised agent has nothing that can push anywhere. The `keys.llm` value *does* go into the
round's container, scoped to that one project and dying with that one round.

Scope the token to the one repository, not the organisation.