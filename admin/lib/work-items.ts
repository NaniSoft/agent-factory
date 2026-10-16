import type { components } from './generated/openapi';

/**
 * The work-item detail's view model and the one call that fetches it.
 *
 * Every type here is an alias over `generated/openapi.d.ts`, which `pnpm openapi` generates
 * from the factory's own OpenAPI document: the work item's header, every round it has run
 * with the host's diff, the decisions a reviewer made and the decisions still offered, and
 * how it ended. The `AgentFactory.Api.WorkItemDetailView` records are what the document is
 * generated from, so a rename there fails `tsc` here rather than reading undefined at
 * runtime; the field-level notes that used to sit on these types live on those records now.
 */

/**
 * One file of a diff: its path, what git says happened, the line counts, and git's own text.
 * `open` is whether the section starts open — the first file is open and the rest are not.
 */
export type DiffFileView = components['schemas']['DiffFileView'];

/**
 * One round's change, as the factory judged it. `state` is one of `shown`, `empty`,
 * `unfinished` or `unavailable`, and the renderer must tell them apart: `empty` is a round
 * that ran to completion and changed nothing, `unfinished` is a round that did not run to
 * completion, and `unavailable` is a diff that could not be generated at all.
 */
export type DiffOnTheBoardView = components['schemas']['DiffOnTheBoardView'];

/** One round as the detail renders it: its outcome, its payload, its log and its change. */
export type RoundOnTheBoardView = components['schemas']['RoundOnTheBoardView'];

/** One decision the reviewer made, and the lane the loop put the work item in to apply it. */
export type DecisionMadeView = components['schemas']['DecisionMadeView'];

/** The work item's own header. */
export type WorkItemHeaderView = components['schemas']['WorkItemHeaderView'];

/**
 * What the routing round decided about a work item, as the detail renders it: the whole of
 * the routing judgement. `say` is the factory's one sentence for the kind, `kind` is the
 * verdict's own name, `reason` is the round's own reason (null for a work item nobody has
 * routed), and `issue` is the issue the factory filed for the verdict as its number, or
 * null when nothing was filed.
 */
export type RouteView = components['schemas']['RouteView'];

export type WorkItemDetailView = components['schemas']['WorkItemDetailView'];

/**
 * Read one work item's whole record.
 *
 * The API is the factory's own process and lives at `/api/work-items/{id}`, a sibling of
 * the Board app the same process serves at the root. `cache: 'no-store'` is the point of
 * a detail read: the rounds and decisions
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
