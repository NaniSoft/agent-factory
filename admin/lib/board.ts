import type { components } from './generated/openapi';

/**
 * The Board's view model and the one call that fetches it.
 *
 * Every type here is an alias over `generated/openapi.d.ts`, which `pnpm openapi` generates
 * from the factory's own OpenAPI document — the `AgentFactory.Api.BoardView` shape, carried
 * into that document by the endpoint's `.Produces<T>()`. So the factory is the only place
 * the shape is written down: a field renamed there fails `tsc` here rather than reading
 * `undefined` on a reviewer's screen, and the field-level notes that used to be on these
 * types now live on the records in `src/AgentFactory.Application/Api/`.
 */

/** One work item as a card: the factory's own judgement, drawn and not re-decided. */
export type CardView = components['schemas']['CardView'];

/** One lane, as a column: its slug, its words, and the cards standing in it. */
export type LaneView = components['schemas']['LaneView'];

/** One project the factory serves. Credentials are names and never values. */
export type ProjectView = components['schemas']['ProjectView'];

/** One project file the loader refused, and why. A refusal is reported, not swallowed. */
export type RejectionView = components['schemas']['ProjectFileRejectionView'];

/**
 * One project's intake as the factory judged it: the state, what was found or what was
 * refused, and when this project will be read again. Every field is the factory's own
 * judgement — the state's slug, the failure's classification, the moment — so the
 * renderer draws the row and composes nothing.
 */
export type IntakeRowView = components['schemas']['IntakeRowView'];

/**
 * The whole of intake, from the factory's own judgement: the section's worst state, the
 * one line that says whether an empty Backlog can be trusted, the counts, and one row per
 * served project. It is not narrowed by the project filter — a filter that could hide the
 * reason there is nothing on the board could make a broken factory look like a working one.
 */
export type IntakeView = components['schemas']['IntakeView'];

export type BoardView = components['schemas']['BoardView'];

/** What accepting a Backlog work item came to: whether the loop moved it, and its refusal. */
export type AcceptanceResult = components['schemas']['AcceptanceResult'];

/**
 * Read the factory's board.
 *
 * The API is the factory's own process and lives at `/api/board`, a sibling of the
 * Board app the same process serves at the root. `cache: 'no-store'` is the point of a
 * board: every read reflects the machine now, and a cached read would be a claim
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

