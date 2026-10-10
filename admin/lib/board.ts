/**
 * The Board's view model and the one call that fetches it.
 *
 * The shape mirrors `AgentFactory.Api.BoardView`: the container budget and the
 * auto-merge mode, the lanes and the work-item cards in them, the projects the
 * factory serves and the files it refused. Every field is camel-cased by name on
 * both sides and pinned there rather than left to a serialiser's naming policy.
 * There is deliberately no codegen between the two — the design system's gate kit
 * is the contract on this side and the C# records are the contract on the other —
 * so a fixture that mirrors the endpoint's own response (asserted by the renderer
 * test) is what keeps them in step.
 */

/**
 * A review workspace as the card and the open endpoint report it: whether one is
 * open, where it is, how long it has left in whole seconds, and the factory's own
 * words when it could not be opened. `remainingSeconds` is present only while a
 * workspace is active — the endpoint clamps a workspace past its lifetime to zero
 * rather than handing the renderer a negative.
 */
export type WorkspaceView = {
  active: boolean;
  url: string | null;
  remainingSeconds: number | null;
  error: string | null;
};

/** One work item as a card: the factory's own judgement, drawn and not re-decided. */
export type CardView = {
  id: string;
  project: string;
  issueNumber: number;
  title: string;
  /** The lane's slug, matching the column the card stands in. */
  lane: string;
  /** The lane's words, matching the column's label. */
  laneLabel: string;
  roundCount: number;
  /** The ceiling the count is read against, from the factory's own constant. */
  roundCeiling: number;
  /** Why the work item ended where it did, or empty for a lane that is a stage. */
  ending: string;
  /** The decision slugs a reviewer may still post, from the factory's own set. */
  decisions: string[];
  /** The workspace open for this work item, or null when none has been asked for. */
  workspace: WorkspaceView | null;
  /**
   * When this card's silence would merge it, as ISO-8601, or null. The factory sends it
   * only for a card in Review with auto-merge on, because a countdown a reviewer cannot
   * see per work item is one they cannot act on while it still matters.
   */
  autoMergeAt: string | null;
};

/** One lane, as a column: its slug, its words, and the cards standing in it. */
export type LaneView = {
  lane: string;
  label: string;
  cards: CardView[];
};

/** One project the factory serves. Credentials are names and never values. */
export type ProjectView = {
  name: string;
  repoUrl: string;
  workerImage: string;
  llmModel: string;
  githubKeyName: string;
  llmKeyName: string;
  sourceFile: string;
};

/** One project file the loader refused, and why. A refusal is reported, not swallowed. */
export type RejectionView = {
  fileName: string;
  reason: string;
  message: string;
};

/**
 * One project's intake as the factory judged it: the state, what was found or what was
 * refused, and when this project will be read again. Every field is the factory's own
 * judgement — the state's slug, the failure's classification, the moment — so the
 * renderer draws the row and composes nothing.
 */
export type IntakeRowView = {
  project: string;
  repoUrl: string;
  /** The state's slug: `never-polled`, `polled` or `failing`. */
  status: string;
  /** How many open issues the last successful read found, or null where there was none. */
  openIssues: number | null;
  /** The failure's classification, or null when the read succeeded. */
  failure: string | null;
  /** What the failed turn said, in GitHub's own words. */
  because: string | null;
  /** When the last turn finished, either way, as ISO-8601, or null. */
  atUtc: string | null;
  /** How many times in a row this project has failed. */
  failures: number;
  /** When this project will next be read, as ISO-8601, or null. */
  againAfterUtc: string | null;
  /** `never` for a permanent failure, a moment for a transient one, `next-pass` otherwise. */
  again: string;
  /** The row's sentence, in the reviewer's words. */
  says: string;
};

/**
 * The whole of intake, from the factory's own judgement: the section's worst state, the
 * one line that says whether an empty Backlog can be trusted, the counts, and one row per
 * served project. It is not narrowed by the project filter — a filter that could hide the
 * reason there is nothing on the board could make a broken factory look like a working one.
 */
export type IntakeView = {
  /** The worst state of any project: `never-polled`, `polled` or `failing`. */
  status: string;
  /** The one line above the rows, from the factory's own summary. */
  summary: string;
  projects: number;
  polled: number;
  neverPolled: number;
  failing: number;
  rows: IntakeRowView[];
};

export type BoardView = {
  budget: { inUse: number; of: number };
  autoMerge: boolean;
  lanes: LaneView[];
  /** The set the factory is serving — what the board header and intake are about. */
  projects: ProjectView[];
  /**
   * The projects the filter offers: the served set plus any project that still has work
   * items from a file that has since been removed. A removed project's history is still a
   * reviewer's to judge, so the filter has to reach it.
   */
  filterProjects: string[];
  rejections: RejectionView[];
  intake: IntakeView;
};

/** What accepting a Backlog work item came to: whether the loop moved it, and its refusal. */
export type AcceptanceResult = {
  applied: boolean;
  refusal: string | null;
};

/**
 * Read the factory's board.
 *
 * The API is the factory's own process and lives at `/api/board`, a sibling of the
 * Board app the same process serves at the root. `cache: 'no-store'` is the point of a
 * board: every read reflects the machine now, and a cached budget would be a claim
 * about a moment that has passed.
 */
export async function fetchBoard(signal?: AbortSignal): Promise<BoardView> {
  const response = await fetch('/api/board', { cache: 'no-store', signal });
  if (!response.ok) {
    throw new Error(`the factory answered ${response.status}`);
  }

  return (await response.json()) as BoardView;
}

/**
 * Open (or answer with) the review workspace for a work item.
 *
 * The call the card's "Open workspace" action makes. It spawns a container and moves
 * nothing, so a re-read of the board is all that is needed to reflect it. Every
 * failure the factory distinguishes — not in Review, no project file served, no lifted
 * tree, port exhaustion — comes back in the same body as an `error` on an inactive
 * view rather than as a non-2xx, so the caller renders the factory's words on the card
 * whatever happened. Only a transport fault or a non-2xx throws.
 */
/**
 * Accept a Backlog work item into the build.
 *
 * The call the card's "Accept into build" control makes. It is the restored Backlog gate
 * (#40): intake puts every issue in Backlog and nothing is built until a reviewer accepts
 * it. The loop moves the work item; a work item not waiting in Backlog comes back
 * `applied:false` with the factory's own words in `refusal`, rendered on the card.
 */
export async function acceptWorkItem(workItemId: string): Promise<AcceptanceResult> {
  const response = await fetch(`/api/work-items/${encodeURIComponent(workItemId)}/accept`, {
    method: 'POST',
  });
  if (!response.ok) {
    throw new Error(`the factory answered ${response.status}`);
  }

  return (await response.json()) as AcceptanceResult;
}

/**
 * Open (or answer with) the review workspace for a work item.
 *
 * The call the card's "Open workspace" action makes. It spawns a container and moves
 * nothing, so a re-read of the board is all that is needed to reflect it. Every
 * failure the factory distinguishes — not in Review, no project file served, no lifted
 * tree, port exhaustion — comes back in the same body as an `error` on an inactive
 * view rather than as a non-2xx, so the caller renders the factory's words on the card
 * whatever happened. Only a transport fault or a non-2xx throws.
 */
export async function openWorkspace(workItemId: string): Promise<WorkspaceView> {
  const response = await fetch(`/api/work-items/${encodeURIComponent(workItemId)}/workspace`, {
    method: 'POST',
  });
  if (!response.ok) {
    throw new Error(`the factory answered ${response.status}`);
  }

  return (await response.json()) as WorkspaceView;
}
