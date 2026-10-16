'use client';

import { useCallback, useId, useState } from 'react';

import { Alert, AlertDescription, AlertTitle } from '@nanisoft/prism-ui/components/alert';
import { Button } from '@nanisoft/prism-ui/components/button';
import { Label } from '@nanisoft/prism-ui/components/label';
import { Textarea } from '@nanisoft/prism-ui/components/textarea';

import { postDecision, type DecisionResult } from '@/lib/decisions';

/**
 * The decisions, in the factory's own slugs, as the words a button reads. The slug is
 * the wire format and it is what the offered set carries; the words are this app's,
 * because the factory names no button.
 */
const LABELS: Record<string, string> = {
  approve: 'Approve',
  'request-changes': 'Request changes',
  reject: 'Reject',
};

/**
 * The tone each decision is drawn in. Only the decline is destructive: an approve and
 * a request for changes are both ordinary things a reviewer does, and the one control
 * that finishes a work item for good is the one that earns the alarm colour.
 */
const TONES: Record<string, 'default' | 'destructive'> = {
  reject: 'destructive',
};

export type DecisionFormProps = {
  /** The work item the decision is about, as the endpoint's path segment. */
  workItemId: string;
  /** The decisions the factory offers here, from the card's own `decisions` set. */
  decisions: readonly string[];
  /**
   * The factory's own words for what it refused, if anything, since the last decision.
   * Held by the caller rather than in here, because a refusal has to survive the card
   * moving lane: an approval the merge could not carry out lands the work item in
   * Escalated, and the refusal is still rendered where the reviewer acted.
   */
  refusal?: string | null;
  /** What to do with the factory's answer, whether it applied the decision or refused it. */
  onResult: (result: DecisionResult) => void;
};

/**
 * The three decisions as one form, wherever a reviewer can still act.
 *
 * It is one component rather than one per surface so the Board and the work-item detail
 * offer the decision on exactly the same terms: the offered set is drawn as buttons and
 * nothing else, a request for changes carries the reviewer's words, and the factory's
 * refusal is rendered as it wrote it. The component decides nothing: which decisions are
 * offered is `Decisions.OfferedIn`'s answer, serialised onto the card, and whether a
 * decision was carried out is the loop's answer, returned by the endpoint. It never
 * blocks a request for changes with no words on its own — that refusal is the store's,
 * and the store's words are what the reviewer is owed.
 */
export function DecisionForm({ workItemId, decisions, refusal, onResult }: DecisionFormProps) {
  const [feedback, setFeedback] = useState('');
  const [busy, setBusy] = useState(false);
  const feedbackId = useId();

  const decide = useCallback(
    (decision: string) => {
      setBusy(true);
      postDecision(workItemId, decision, feedback)
        .then((result) => {
          // The reviewer's words are kept only when the factory took them: a refused
          // request for changes is refused because they are missing, and clearing the
          // box would hide the reason the reviewer is being asked for them.
          if (result.applied) setFeedback('');
          onResult(result);
        })
        .catch((error: unknown) => {
          onResult({
            applied: false,
            refusal: error instanceof Error ? error.message : 'the factory could not be reached',
            resultingLane: '',
          });
        })
        .finally(() => setBusy(false));
    },
    [feedback, onResult, workItemId],
  );

  if (decisions.length === 0) {
    return null;
  }

  const sendsBack = decisions.includes('request-changes');

  return (
    <div className="decisions" role="group" aria-label="Decisions">
      {sendsBack ? (
        <div className="decisions__field">
          <Label htmlFor={feedbackId}>Why send it back?</Label>
          <Textarea
            id={feedbackId}
            value={feedback}
            onChange={(event) => setFeedback(event.target.value)}
            placeholder="What has to change before this is right."
          />
        </div>
      ) : null}

      <div className="decisions__actions">
        {decisions.map((decision) => (
          <Button
            key={decision}
            type="button"
            size="sm"
            variant={TONES[decision] ?? 'default'}
            disabled={busy}
            onClick={() => decide(decision)}
          >
            {LABELS[decision] ?? decision}
          </Button>
        ))}
      </div>

      {refusal ? (
        <div className="decisions__refusal">
          <Alert variant="destructive">
            <AlertTitle>The factory refused</AlertTitle>
            <AlertDescription>{refusal}</AlertDescription>
          </Alert>
        </div>
      ) : null}
    </div>
  );
}
