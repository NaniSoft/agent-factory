'use client';

import { Suspense, useCallback, useEffect, useState } from 'react';
import { useSearchParams } from 'next/navigation';

import { Badge } from '@nanisoft/prism-ui/components/badge';
import { Button } from '@nanisoft/prism-ui/components/button';
import { Card, CardContent, CardHeader, CardTitle } from '@nanisoft/prism-ui/components/card';
import { CtaLink } from '@nanisoft/prism-ui/components/cta-link';
import { Skeleton } from '@nanisoft/prism-ui/components/skeleton';
import { Status, type StatusTone } from '@nanisoft/prism-ui/components/status';
import { EmptyState01 } from '@nanisoft/prism-ui/blocks/empty-state-01';

import { DecisionForm } from '@/components/decision-form';
import type { DecisionResult } from '@/lib/decisions';
import {
  fetchWorkItem,
  type DecisionMadeView,
  type DiffOnTheBoardView,
  type RoundOnTheBoardView,
  type WorkItemDetailView,
} from '@/lib/work-items';

/**
 * The tone a round's outcome is drawn in. `Produced` is the round that ran to completion,
 * whether or not its change was any good; `Failed` is a round that did not finish; a
 * timeout is its own warning. The tone is Prism's and the words beside it are the factory's,
 * so the dot never stands alone.
 */
const OUTCOME_TONE: Record<string, StatusTone> = {
  Produced: 'success',
  Failed: 'destructive',
  TimedOut: 'warning',
};

/**
 * The tone and the words a diff's state is drawn with. The four states are four different
 * claims — a change, a round that ran and changed nothing, a round that did not finish, and
 * a diff that could not be generated — and the page must not collapse them.
 */
const DIFF_STATE: Record<string, { tone: StatusTone; label: string }> = {
  shown: { tone: 'info', label: 'a change' },
  empty: { tone: 'neutral', label: 'changed nothing' },
  unfinished: { tone: 'warning', label: 'did not finish' },
  unavailable: { tone: 'destructive', label: 'no diff' },
};

/**
 * What the page has read so far. Four states rather than a nullable detail and a boolean:
 * a fetch that has not answered, one that answered, one that failed, and a page opened
 * with no work item named at all are four different things a reader is owed.
 */
type Reading =
  | { phase: 'loading' }
  | { phase: 'ready'; detail: WorkItemDetailView }
  | { phase: 'error'; message: string };

/** The detail's own cadence, the same five seconds the Board polls its endpoint on. */
const POLL_MS = 5000;

/**
 * The work-item detail.
 *
 * A client component, and necessarily so: the app is a static export with no server, and
 * the only thing that knows a work item's rounds and decisions is the factory's own
 * loopback API. It re-decides nothing — the diff's four states, the ending, the route and
 * the offered decisions are the factory's own judgement, serialised, drawn through Prism.
 * The id travels in `?item=<id>` because a static export cannot emit an unbounded dynamic
 * segment.
 */
export default function WorkItemPage() {
  return (
    <main className="page">
      <header className="page__header">
        <p className="work-item__back">
          <CtaLink href="/" variant="ghost" size="sm">
            Back to the Board
          </CtaLink>
        </p>
        <h1 className="page__title">The work item</h1>
        <p className="page__lede">
          One issue, every round the factory has run for it, the change each round left, and what a
          reviewer decided about it.
        </p>
      </header>

      {/*
        `useSearchParams` reads the work item's id, which the app is a static export and so
        is a query string rather than a server render. A static export cannot know it at build
        time, so the reading is deferred behind this boundary and the shell is still emitted
        for a reader with scripting off.
      */}
      <Suspense fallback={<DetailLoading />}>
        <WorkItemBody />
      </Suspense>
    </main>
  );
}

/** The detail, read from the factory and drawn. Split from the page so the id is read inside the boundary. */
function WorkItemBody() {
  const item = useSearchParams().get('item');
  const [reading, setReading] = useState<Reading>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);

  /**
   * What the factory refused, in its own words, since the last decision. Held here rather
   * than in the form because a refusal has to survive the work item moving lane: an
   * approval the merge could not carry out parks the work item in Escalated, and the
   * loop's own words are still rendered where the reviewer acted.
   */
  const [refusal, setRefusal] = useState<string | null>(null);

  useEffect(() => {
    if (!item) {
      return;
    }

    let cancelled = false;
    const controller = new AbortController();
    const id = item;

    async function read() {
      try {
        const detail = await fetchWorkItem(id, controller.signal);
        if (!cancelled) setReading({ phase: 'ready', detail });
      } catch (error: unknown) {
        if (!cancelled && !controller.signal.aborted) {
          setReading({
            phase: 'error',
            message: error instanceof Error ? error.message : 'the factory could not be reached',
          });
        }
      }
    }

    void read();

    // The detail polls its own endpoint, and pauses while the tab is hidden: the rounds and
    // decisions reflect the machine now, and a hidden tab that keeps reading is one that
    // keeps the factory answering for nobody. Coming back to the tab reads once, so the
    // reader meets the machine now rather than five seconds ago.
    const interval = window.setInterval(() => {
      if (!document.hidden) void read();
    }, POLL_MS);

    const onVisibilityChange = () => {
      if (!document.hidden) void read();
    };
    document.addEventListener('visibilitychange', onVisibilityChange);

    return () => {
      cancelled = true;
      controller.abort();
      window.clearInterval(interval);
      document.removeEventListener('visibilitychange', onVisibilityChange);
    };
  }, [item, attempt]);

  const retry = useCallback(() => {
    setReading({ phase: 'loading' });
    setAttempt((count) => count + 1);
  }, []);

  // A decision was carried out or refused. Either way the factory is read again now,
  // because a decision that was carried out has moved the work item and the resulting lane
  // belongs on the next render rather than a poll away.
  const onDecisionResult = useCallback((result: DecisionResult) => {
    setRefusal(result.refusal);
    setAttempt((count) => count + 1);
  }, []);

  // The id is read during render rather than set from an effect: a page opened with no work
  // item named is a shape of the address, not a state the fetch can reach.
  if (!item) {
    return (
      <EmptyState01
        reason="no-match"
        title="No work item was named"
        body="This page reads a work item from its id, and none was given. Open one from the Board."
      />
    );
  }

  if (reading.phase === 'loading') {
    return <DetailLoading />;
  }

  if (reading.phase === 'error') {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Could not read the work item</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="page__error">
            <Status tone="destructive" label={reading.message} />
            <Button variant="outline" onClick={retry}>
              Try again
            </Button>
          </div>
        </CardContent>
      </Card>
    );
  }

  return <Detail detail={reading.detail} refusal={refusal} onDecisionResult={onDecisionResult} />;
}

/** The work item once the factory has answered: the header, the rounds and the decisions. */
function Detail({
  detail,
  refusal,
  onDecisionResult,
}: {
  detail: WorkItemDetailView;
  refusal: string | null;
  onDecisionResult: (result: DecisionResult) => void;
}) {
  const { workItem } = detail;

  return (
    <div className="work-item" data-work-item={workItem.id}>
      <Card className="work-item__header">
        <CardHeader>
          <CardTitle>
            #{workItem.issueNumber} {workItem.title}
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div className="work-item__tags">
            <Badge variant="outline">{workItem.project}</Badge>
            <Badge variant="outline">{workItem.laneLabel}</Badge>
            <Badge variant="outline">
              round {workItem.roundCount} of {workItem.roundCeiling}
            </Badge>
          </div>

          {/* Why it ended where it did, for the lanes that are an ending. Empty otherwise. */}
          {detail.ending ? (
            <p className="work-item__ending" data-ending>
              {detail.ending}
            </p>
          ) : null}

          <p className="work-item__meta">
            base <code>{workItem.baseBranch}</code> · {workItem.repoUrl}
          </p>
        </CardContent>
      </Card>

      <section className="work-item__rounds" aria-label="Rounds">
        <h2 className="work-item__section-title">Rounds</h2>
        {detail.rounds.length === 0 ? (
          <p className="work-item__empty">No round has run for this work item yet.</p>
        ) : (
          <ol className="work-item__round-list">
            {detail.rounds.map((round) => (
              <li key={round.roundNumber}>
                <RoundCard round={round} />
              </li>
            ))}
          </ol>
        )}
      </section>

      <Decisions detail={detail} refusal={refusal} onDecisionResult={onDecisionResult} />
    </div>
  );
}

/**
 * One round: its outcome and failure, the change it left, what its container recorded, and
 * its log. The change is the diff the factory generated on the host from the tree the round
 * left, and it is separate from the payload because the payload is the container's account
 * of itself and can be missing or bounded.
 */
function RoundCard({ round }: { round: RoundOnTheBoardView }) {
  return (
    <Card
      className="work-item__round"
      data-round={round.roundNumber}
      data-outcome={round.outcome}
      data-failure={round.failure ?? ''}
    >
      <CardHeader>
        <CardTitle as="h3">Round {round.roundNumber}</CardTitle>
        <div className="work-item__round-meta">
          <Status tone={OUTCOME_TONE[round.outcome] ?? 'neutral'} label={round.outcome} size="sm" />
          {round.attempts > 1 ? (
            <span className="work-item__attempts">{round.attempts} attempts</span>
          ) : null}
          {round.failure ? (
            <Status tone="destructive" label={`${round.failure.toLowerCase()} failure`} size="sm" />
          ) : null}
        </div>
      </CardHeader>
      <CardContent>
        <RoundDiff diff={round.diff} />

        {round.payload ? (
          <details className="work-item__payload" open={round.diff === null}>
            <summary>what the round's container recorded about itself</summary>
            <pre className="work-item__pre" data-result={round.roundNumber}>
              {round.payload}
            </pre>
          </details>
        ) : null}

        {round.agentNote ? (
          <p className="work-item__note" data-note={round.roundNumber}>
            {round.agentNote}
          </p>
        ) : null}

        {round.log ? (
          <details className="work-item__log" open={!round.payload}>
            <summary>the round's log</summary>
            <pre className="work-item__pre" data-log={round.roundNumber}>
              {round.log}
            </pre>
          </details>
        ) : null}
      </CardContent>
    </Card>
  );
}

/**
 * One round's change. The four states are four different claims and are rendered four
 * different ways: a round that did not finish must never read as a round that changed
 * nothing, and a diff that could not be generated must say so rather than render an empty
 * section. Every file is a collapsible section with the path, what git says happened and the
 * line counts on the summary line, the first open and the rest closed — and git's own text
 * underneath, unchanged, so a reviewer can hold it against their own `git diff`.
 */
function RoundDiff({ diff }: { diff: DiffOnTheBoardView | null }) {
  if (diff === null) {
    return (
      <p className="work-item__diff-none" data-diff-state="none">
        No diff is on record for this round.
      </p>
    );
  }

  if (diff.state === 'unavailable') {
    return (
      <section
        className="work-item__diff"
        data-diff
        data-diff-state="unavailable"
        data-tree={diff.tree}
      >
        <p className="work-item__diff-says" data-diff-says>
          There is no diff for this round: {diff.unavailableBecause}
        </p>
      </section>
    );
  }

  if (diff.state === 'empty' || diff.state === 'unfinished') {
    // The same unchanged disk, said two different ways: the factory's own sentence, which
    // branches on the round's outcome rather than on anything the diff can be asked.
    return (
      <section
        className="work-item__diff"
        data-diff
        data-diff-state={diff.state}
        data-tree={diff.tree}
      >
        <div className="work-item__diff-state">
          <Status tone={DIFF_STATE[diff.state]?.tone ?? 'neutral'} label={DIFF_STATE[diff.state]?.label ?? diff.state} size="sm" />
        </div>
        <p className="work-item__diff-says" data-diff-says>
          {diff.saysAboutAnUnchangedDisk}
        </p>
      </section>
    );
  }

  return (
    <section
      className="work-item__diff"
      data-diff
      data-diff-state="shown"
      data-files={diff.files.length}
      data-total-files={diff.totalFiles}
      data-omitted-files={diff.omittedFiles}
      data-omitted-lines={diff.omittedLines}
      data-tree={diff.tree}
    >
      <div className="work-item__diff-state">
        <Status tone={DIFF_STATE.shown?.tone ?? 'info'} label={DIFF_STATE.shown?.label ?? 'a change'} size="sm" />
        <span className="work-item__diff-counts">
          {diff.files.length} of {diff.totalFiles} file(s) shown
        </span>
      </div>

      {/* What a bounded diff left off, counted and pointed at the tree. */}
      {diff.whatIsLeftOff ? (
        <p className="work-item__diff-omitted" data-diff-omitted>
          {diff.whatIsLeftOff}
        </p>
      ) : null}

      {/* Why the container's own copy is not the one on the page. */}
      {diff.boundedElsewhere ? (
        <p className="work-item__diff-bounded" data-diff-bounded>
          {diff.boundedElsewhere}
        </p>
      ) : null}

      <ol className="work-item__diff-files">
        {diff.files.map((file) => (
          <li key={file.path}>
            <details
              className="work-item__diff-file"
              data-diff-file={file.path}
              data-change={file.change}
              data-added={file.added}
              data-removed={file.removed}
              data-binary={file.binary ? 'true' : 'false'}
              data-open={file.open ? 'true' : 'false'}
              open={file.open}
            >
              <summary className="work-item__diff-summary">
                <span className="work-item__diff-path">{file.path}</span>
                <span className="work-item__diff-change">{file.change}</span>
                {file.previousPath ? (
                  <span className="work-item__diff-from">from {file.previousPath}</span>
                ) : null}
                {file.binary ? (
                  <span className="work-item__diff-lines">binary, so no line counts</span>
                ) : (
                  <span className="work-item__diff-lines">
                    +{file.added} −{file.removed}
                  </span>
                )}
              </summary>
              <pre className="work-item__diff-text">{file.text}</pre>
            </details>
          </li>
        ))}
      </ol>
    </section>
  );
}

/**
 * The decisions: what the reviewer did, and the form that offers what they may still do.
 *
 * The form is the shared one from the Board, so a decision is offered and refused on exactly
 * the same terms wherever a reviewer is looking. Which decisions are offered is the
 * factory's own set, serialised onto the detail — a work item in Review offers three, a
 * parked one offers two, and a final one offers none.
 */
function Decisions({
  detail,
  refusal,
  onDecisionResult,
}: {
  detail: WorkItemDetailView;
  refusal: string | null;
  onDecisionResult: (result: DecisionResult) => void;
}) {
  return (
    <section className="work-item__decisions" aria-label="Decisions">
      <h2 className="work-item__section-title">Decisions</h2>

      {detail.decisions.length === 0 ? (
        <p className="work-item__empty">No decision has been made about this work item yet.</p>
      ) : (
        <ol className="work-item__decided">
          {detail.decisions.map((decision) => (
            <Decided key={decision.sequence} decision={decision} />
          ))}
        </ol>
      )}

      {detail.offeredDecisions.length > 0 ? (
        <DecisionForm
          workItemId={detail.workItem.id}
          decisions={detail.offeredDecisions}
          refusal={refusal}
          onResult={onDecisionResult}
        />
      ) : null}
    </section>
  );
}

/** One decision the reviewer made, in their own words, and where the loop put the work item. */
function Decided({ decision }: { decision: DecisionMadeView }) {
  return (
    <li
      className="work-item__decision"
      data-decision-made={decision.decision}
      data-applied-to={decision.appliedTo ?? ''}
    >
      <div className="work-item__decision-head">
        <Badge variant={decision.decision === 'reject' ? 'destructive' : 'outline'}>
          {decision.decision}
        </Badge>
        <span className="work-item__decision-applied">
          {decision.appliedToLabel ? `applied to ${decision.appliedToLabel}` : 'not applied yet'}
        </span>
      </div>
      {decision.feedback ? (
        <p className="work-item__decision-feedback">{decision.feedback}</p>
      ) : null}
    </li>
  );
}

/** The detail before the factory has answered: the shape of it, in Prism skeletons. */
function DetailLoading() {
  return (
    <div className="work-item__loading">
      <Status tone="info" label="Reading the work item" />
      <Skeleton className="work-item__loading-header" />
      <Skeleton className="work-item__loading-round" />
      <Skeleton className="work-item__loading-round" />
    </div>
  );
}
