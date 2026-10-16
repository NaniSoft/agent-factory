import type { components } from './generated/openapi';

/**
 * The Decisions view model and the one call that posts one.
 *
 * `DecisionResult` is an alias over `generated/openapi.d.ts` — the factory's own
 * `AgentFactory.Api.DecisionResult`, carried into that document by the endpoint's
 * `.Produces<T>()` — so the shape is written down once. Nothing here decides anything:
 * `applied` and `refusal` are the loop's answer, and a refusal is rendered as it was
 * written.
 */
export type DecisionResult = components['schemas']['DecisionResult'];

/**
 * Post one decision about one work item, and read the factory's answer.
 *
 * The API is the factory's own process and lives at
 * `/api/work-items/{id}/decisions`, a sibling of the Board app the same process serves
 * at the root. A refusal is a 200 with `applied: false` and the words
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
