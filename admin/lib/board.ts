/**
 * The Board's view model and the one call that fetches it.
 *
 * The shape mirrors `AgentFactory.Api.BoardView`: the container budget and the
 * auto-merge mode, camel-cased by name on both sides and pinned there rather than
 * left to a serialiser's naming policy. There is deliberately no codegen between
 * the two — the design system's gate kit is the contract on this side and the C#
 * records are the contract on the other — so a fixture that mirrors the endpoint's
 * own response (asserted by the renderer test) is what keeps them in step.
 */
export type BoardView = {
  budget: { inUse: number; of: number };
  autoMerge: boolean;
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
