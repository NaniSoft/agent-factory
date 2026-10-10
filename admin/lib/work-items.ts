/**
 * The work-item detail's view model and the one call that fetches it.
 *
 * The shape mirrors `AgentFactory.Api.WorkItemDetailView`: the work item's header, every
 * round it has run with the host's diff, the decisions a reviewer made and the decisions
 * still offered, how it ended, and its route. Every field is camel-cased by name on both
 * sides and pinned there rather than left to a serialiser's naming policy. There is
 * deliberately no codegen between the two — the C# records are the contract on one side and
 * a fixture mirroring the endpoint's own response is what keeps them in step on this side.
 */

/**
 * One file of a diff: its path, what git says happened, the line counts, and git's own text.
 * `open` is whether the section starts open — the first file is open and the rest are not.
 */
export type DiffFileView = {
  path: string;
  previousPath: string | null;
  /** What git says happened: `added`, `modified`, `deleted`, `renamed`, `copied`, ... */
  change: string;
  added: number;
  removed: number;
  binary: boolean;
  open: boolean;
  /** Git's own text for this file's section, byte for byte. */
  text: string;
};

/**
 * One round's change, as the factory judged it. `state` is one of `shown`, `empty`,
 * `unfinished` or `unavailable`, and the renderer must tell them apart: `empty` is a round
 * that ran to completion and changed nothing, `unfinished` is a round that did not run to
 * completion, and `unavailable` is a diff that could not be generated at all.
 */
export type DiffOnTheBoardView = {
  state: string;
  files: DiffFileView[];
  omittedFiles: number;
  omittedLines: number;
  totalFiles: number;
  totalLines: number;
  tree: string;
  unavailableBecause: string | null;
  boundedElsewhere: string | null;
  /** The factory's own sentence for an empty or unfinished disk, or null when there is a change. */
  saysAboutAnUnchangedDisk: string | null;
  /** What a bounded diff left off the page and where the rest is, or null when it is whole. */
  whatIsLeftOff: string | null;
};

/** One round as the detail renders it: its outcome, its payload, its log and its change. */
export type RoundOnTheBoardView = {
  roundNumber: number;
  outcome: string;
  failure: string | null;
  attempts: number;
  startedUtc: string;
  completedUtc: string;
  payload: string | null;
  agentNote: string | null;
  log: string | null;
  diff: DiffOnTheBoardView | null;
};

/** One decision the reviewer made, and the lane the loop put the work item in to apply it. */
export type DecisionMadeView = {
  sequence: number;
  decision: string;
  feedback: string;
  decidedUtc: string;
  appliedTo: string | null;
  appliedToLabel: string | null;
};

/** What the factory decided about this issue's kind, and whether acceptance is offered. */
export type RouteView = {
  kind: string;
  kindLabel: string;
  say: string;
  offersAcceptance: boolean;
};

/** The work item's own header. */
export type WorkItemHeaderView = {
  id: string;
  project: string;
  repoUrl: string;
  issueNumber: number;
  title: string;
  body: string;
  baseBranch: string;
  lane: string;
  laneLabel: string;
  kind: string;
  kindLabel: string;
  roundCount: number;
  roundCeiling: number;
  createdUtc: string;
  updatedUtc: string;
  reviewStartedUtc: string | null;
  mergeAttempts: number;
};

export type WorkItemDetailView = {
  workItem: WorkItemHeaderView;
  rounds: RoundOnTheBoardView[];
  decisions: DecisionMadeView[];
  /** The decision slugs a reviewer may still post, from the factory's own set. */
  offeredDecisions: string[];
  /** Why the work item ended where it did, or empty for a lane that is a stage. */
  ending: string;
  route: RouteView;
};

/**
 * Read one work item's whole record.
 *
 * The API is the factory's own process and lives off this app's base path, at
 * `/api/work-items/{id}`, so the request is absolute from the origin rather than relative
 * to `/admin`. `cache: 'no-store'` is the point of a detail read: the rounds and decisions
 * reflect the machine now, and a cached round would be a claim about a moment that has
 * passed. A 404 means the store has never seen the id, which is a real answer rather than a
 * fault.
 */
export async function fetchWorkItem(id: string, signal?: AbortSignal): Promise<WorkItemDetailView> {
  const response = await fetch(`/api/work-items/${encodeURIComponent(id)}`, {
    cache: 'no-store',
    signal,
  });
  if (!response.ok) {
    throw new Error(`the factory answered ${response.status}`);
  }

  return (await response.json()) as WorkItemDetailView;
}
