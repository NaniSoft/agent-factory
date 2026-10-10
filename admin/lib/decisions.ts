/**
 * The Decisions view model and the one call that posts one.
 *
 * The shape mirrors `AgentFactory.Api.DecisionResult`: whether the loop applied the
 * decision, the factory's own words when it did not, and the lane the work item is
 * in afterwards. Camel-cased by name on both sides and pinned there rather than left
 * to a serialiser's naming policy. Nothing here decides anything: `applied` and
 * `refusal` are the loop's answer, and a refusal is rendered as it was written.
 */
export type DecisionResult = {
  /** Whether the loop applied a transition for the decision. */
  applied: boolean;
  /** The factory's own words for why nothing was applied, or null when it was. */
  refusal: string | null;
  /** The lane the work item is in after the attempt, from the factory's own store. */
  resultingLane: string;
};

/**
 * Post one decision about one work item, and read the factory's answer.
 *
 * The API is the factory's own process and lives off this app's base path, at
 * `/api/work-items/{id}/decisions`, so the request is absolute from the origin rather
 * than relative to `/admin`. A refusal is a 200 with `applied: false` and the words
 * in `refusal` — it is the reviewer's answer, not a fault — so only a non-2xx
 * response is an error here.
 */
export async function postDecision(
  workItemId: string,
  decision: string,
  feedback: string,
): Promise<DecisionResult> {
  const response = await fetch(
    `/api/work-items/${encodeURIComponent(workItemId)}/decisions`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ decision, feedback }),
    },
  );

  if (!response.ok) {
    throw new Error(`the factory answered ${response.status}`);
  }

  return (await response.json()) as DecisionResult;
}
