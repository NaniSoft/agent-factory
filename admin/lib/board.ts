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

export type BoardView = {
  budget: { inUse: number; of: number };
  autoMerge: boolean;
  lanes: LaneView[];
  projects: ProjectView[];
  rejections: RejectionView[];
};

/**
 * Read the factory's board.
 *
 * The API is the factory's own process and lives off this app's base path, at
 * `/api/board`, so the request is absolute from the origin rather than relative to
 * `/admin`. `cache: 'no-store'` is the point of a board: every read reflects the
 * machine now, and a cached budget would be a claim about a moment that has passed.
 */
export async function fetchBoard(signal?: AbortSignal): Promise<BoardView> {
  const response = await fetch('/api/board', { cache: 'no-store', signal });
  if (!response.ok) {
    throw new Error(`the factory answered ${response.status}`);
  }

  return (await response.json()) as BoardView;
}
