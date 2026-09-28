# Agent Factory

The system that turns a project's GitHub issues into reviewed pull requests: one issue
in, one approved pull request out, with a human judging every build in between.

## Language

### The work

**Factory**:
The system itself — the thing that polls, builds, presents, and merges. It serves
many projects and is not one of them.
_Avoid_: agent (means the AI inside a container), worker

**Project**:
One repository the factory builds for, described by a single project file in
`factories/`. A project is a customer of the factory, not a component of it.
_Avoid_: repo, tenant, factory, target

**Project file**:
The `factories/<project>.yaml` file describing one project: its repository, its
credentials, its LLM provider, and its worker image.
_Avoid_: config, config file, manifest

### The chain

**Issue**:
An open issue on a project's repository. The factory's input, and it stays
GitHub's object.
_Avoid_: work item, ticket, task

**Work item**:
The factory's own record of an issue: its status, its rounds, its results. Created
when the poller picks the issue up, and the unit the board shows and the
orchestrator acts on.
_Avoid_: ticket, task, job, card, build

**Round**:
One whole attempt at building a work item. The agent runs in a single fresh worker
container, builds, tests, corrects itself, and returns a result the reviewer can
judge. A work item gets at most three.
_Avoid_: build attempt, iteration, pass, try, turn

**Feedback**:
The reviewer's reasons attached to a work item when they request changes. The next
round receives it as its brief.
_Avoid_: comments, review comments, notes, corrections

**Result**:
What one round produced and handed back: the files changed, the commands run, the
test outcomes. What actually happened, not a prose account of it.
_Avoid_: output, report, summary, writeup

### The loop

**Board**:
The human-facing surface on port 5000. The only place work is approved.
_Avoid_: dashboard, UI, console, inbox

**Decision**:
What a reviewer does with a work item in Review: approve, request changes, or
reject. The only three ways a work item moves.
_Avoid_: verdict, action, outcome

**Escalation**:
The state of a work item that failed permanently or ran out of rounds. It is parked
rather than final — a human can still merge or reject it — and it is rendered on the
board rather than left silent.
_Avoid_: dead-letter (that is the mechanism, not the state), failure, error, dropped, terminal

**Rejection**:
The state of a work item a human has declined. Unlike escalation this is final: the
reviewer said something conclusive about the change.
_Avoid_: escalation (a human decline, not a failure), dead, abandoned

### The build

**Agent**:
The AI doing the building inside a worker container.
_Avoid_: worker, assistant, model, bot

**Worker container**:
The disposable container one round runs in, carrying code-server and the agent. It
exists for exactly one round; nothing survives it.
_Avoid_: sandbox, agent container, build container, environment, workspace

**Reviewer**:
The human who makes decisions on the board.
_Avoid_: user, approver, operator, maintainer

**Orchestrator**:
The component that accepts work items, starts worker containers, and drives rounds.
It never runs commands inside a container itself — that is the agent's job.
_Avoid_: scheduler, controller, runner
