'use client';

import { Suspense, useCallback, useEffect, useState } from 'react';
import { useSearchParams } from 'next/navigation';

import {
  Kanban01,
  type KanbanCard,
  type KanbanColumn,
  type KanbanState,
} from '@nanisoft/prism-ui/blocks/kanban-01';
import { EmptyState01 } from '@nanisoft/prism-ui/blocks/empty-state-01';
import { Badge } from '@nanisoft/prism-ui/components/badge';
import { Button } from '@nanisoft/prism-ui/components/button';
import { Card, CardContent, CardHeader, CardTitle } from '@nanisoft/prism-ui/components/card';
import { CtaLink } from '@nanisoft/prism-ui/components/cta-link';
import { Skeleton } from '@nanisoft/prism-ui/components/skeleton';
import { Status, type StatusTone } from '@nanisoft/prism-ui/components/status';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@nanisoft/prism-ui/components/table';

import { DecisionForm } from '@/components/decision-form';
import {
  fetchBoard,
  openWorkspace,
  type BoardView,
  type CardView,
  type IntakeView,
  type LaneView,
  type WorkspaceView,
} from '@/lib/board';
import type { DecisionResult } from '@/lib/decisions';

/** The board's own cadence, the same five seconds the Razor board refreshes itself on. */
const POLL_MS = 5000;

/**
 * Where a lane sits in the four states the kanban Block draws.
 *
 * The Block's union is deliberately short — blocked, ready, active, done — and the
 * mapping is the caller's, because a board's own vocabulary is longer than four
 * words. The tone is the Block's; the words beside it are the lane's own, from the
 * factory, so the dot never stands alone:
 *
 * - `ready` is muted and means nothing is being asked of the reader yet: Backlog is
 *   waiting for the accept gate, Frontier is queued behind a worker container.
 * - `active` is the cool role that is not an alarm: a round is running, or the work
 *   is in Review waiting on the reader.
 * - `done` is the only finished state, and it is the factory's own claim that a
 *   merge landed rather than that a reviewer approved.
 * - `blocked` is the destructive tone, and it is reserved for the two lanes where
 *   the machine has stopped and a human has to finish it: Escalated and Rejected.
 */
const LANE_STATE: Record<string, KanbanState> = {
  Backlog: 'ready',
  Frontier: 'ready',
  InProgress: 'active',
  Review: 'active',
  Done: 'done',
  Escalated: 'blocked',
  Rejected: 'blocked',
};

/**
 * The tone and the words an intake state is drawn with.
 *
 * The slug is the factory's and the tone is Prism's, exactly as `LANE_STATE` maps a
 * lane: the factory names the three states — `never-polled`, `polled`, `failing` — and
 * the renderer chooses the dot and the words a reader meets. `never-polled` is `neutral`
 * because the factory knows nothing yet and that is not a fault; `polled` is `success`;
 * and `failing` is `destructive`, because it is the one a reader must be able to find,
 * and it is the state that makes an empty Backlog untrustworthy.
 */
const INTAKE_STATE: Record<string, { tone: StatusTone; label: string }> = {
  'never-polled': { tone: 'neutral', label: 'not polled yet' },
  polled: { tone: 'success', label: 'polled' },
  failing: { tone: 'destructive', label: 'failing' },
};

/**
 * What the page has read so far. Three states rather than a nullable board and a
 * boolean: a fetch that has not answered, one that answered, and one that failed
 * are three different things a reader is owed, and a single `error: string | null`
 * makes "still loading" indistinguishable from "loaded nothing".
 */
type Reading =
  | { phase: 'loading' }
  | { phase: 'ready'; board: BoardView }
  | { phase: 'error'; message: string };

/**
 * The Board.
 *
 * A client component, and necessarily so: the app is a static export with no
 * server, and the only thing that knows the machine's state is the factory's own
 * loopback API. It re-decides nothing — the lanes, the cards, the budget and the
 * auto-merge mode are the factory's own judgement, serialised, drawn through Prism.
 */
export default function BoardPage() {
  return (
    <main className="page">
      <header className="page__header">
        <h1 className="page__title">The factory board</h1>
        <p className="page__lede">
          What the factory is doing right now: the lanes and the work item in each of them, how many
          worker containers it is inside, and whether silence can merge a change.
        </p>
      </header>

      {/*
        `useSearchParams` reads the project filter, which the app is a static export
        and so is a query string rather than a server render. A static export cannot
        know it at build time, so the reading is deferred behind this boundary and
        the shell and the footer are still emitted for a reader with scripting off.
      */}
      <Suspense fallback={<BoardLoading />}>
        <BoardBody />
      </Suspense>

      <footer className="board__footer">
        <p>
          Only this machine can decide what merges. The board is bound to loopback, and no change
          reaches a repository except through a reviewer on this machine.
        </p>
      </footer>
    </main>
  );
}

/**
 * The board, read from the factory and drawn. Split from the page so the search
 * parameters are read inside the Suspense boundary, which is what lets the export
 * build at all.
 */
function BoardBody() {
  const project = useSearchParams().get('project');
  const [reading, setReading] = useState<Reading>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);

  /**
   * What the factory last refused, by work item. Held here rather than on the card
   * because a refusal has to survive the card moving lane: an approval the merge could
   * not carry out parks the work item in Escalated, and the loop's own words are still
   * rendered where the reviewer acted.
   */
  const [refusals, setRefusals] = useState<Record<string, string>>({});

  useEffect(() => {
    let cancelled = false;
    const controller = new AbortController();

    async function read() {
      try {
        const board = await fetchBoard(controller.signal);
        if (!cancelled) setReading({ phase: 'ready', board });
      } catch (error) {
        if (!cancelled && !controller.signal.aborted) {
          setReading({
            phase: 'error',
            message: error instanceof Error ? error.message : 'the factory could not be reached',
          });
        }
      }
    }

    void read();

    // The board polls itself, and pauses while the tab is hidden: a board a reader
    // cannot see does not need refreshing, and a hidden tab that keeps reading is a
    // hidden tab that keeps the factory answering for nobody. Coming back to the tab
    // reads once, so the reader is looking at the machine now rather than five
    // seconds ago.
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
  }, [attempt]);

  // The loading state is the initial state and a retry's, and it is set from the
  // event that starts the read rather than from inside the effect: an effect that
  // synchronously set state would start a second render for every fetch.
  const retry = useCallback(() => {
    setReading({ phase: 'loading' });
    setAttempt((count) => count + 1);
  }, []);

  /**
   * What to do with the factory's answer to a decision, whether it applied it or
   * refused it. A refusal is kept against the work item so it is rendered where the
   * reviewer acted; either way the factory is read again now, because a decision that
   * was carried out has moved the work item and the resulting lane belongs on the next
   * render rather than five seconds from now.
   */
  const onDecisionResult = useCallback((workItemId: string, result: DecisionResult) => {
    setRefusals((current) => {
      const next = { ...current };
      if (result.refusal) {
        next[workItemId] = result.refusal;
      } else {
        delete next[workItemId];
      }
      return next;
    });
    setAttempt((count) => count + 1);
  }, []);

  if (reading.phase === 'loading') {
    return <BoardLoading />;
  }

  if (reading.phase === 'error') {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Could not read the factory</CardTitle>
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

  return <BoardReady board={reading.board} project={project} refusals={refusals} onDecisionResult={onDecisionResult} />;
}

/** The board once the factory has answered: the strip, the filter and the lanes. */
function BoardReady({
  board,
  project,
  refusals,
  onDecisionResult,
}: {
  board: BoardView;
  project: string | null;
  refusals: Record<string, string>;
  onDecisionResult: (workItemId: string, result: DecisionResult) => void;
}) {
  const totalCards = board.lanes.reduce((count, lane) => count + lane.cards.length, 0);

  // A GET and a query string, so the filter is a way of looking at the board rather
  // than a write path: it narrows the cards and nothing else. No work item moves, no
  // decision is recorded, and every project the factory serves stays on offer. Intake
  // is not on this surface and so cannot be narrowed by it.
  const lanes: LaneView[] = board.lanes.map((lane) => ({
    ...lane,
    cards: project ? lane.cards.filter((card) => card.project === project) : lane.cards,
  }));
  const visibleCards = lanes.reduce((count, lane) => count + lane.cards.length, 0);

  const known = project === null || board.projects.some((served) => served.name === project);

  return (
    <>
      <BoardStrip board={board} />

      <ProjectFilter board={board} project={project} />

      {project !== null ? (
        <p className="board__in-force" data-project-in-force={project}>
          Showing only <strong>{project}</strong>.
          {known ? null : (
            <span> The factory has never heard of it: no project by that name is served.</span>
          )}
        </p>
      ) : null}

      {/*
        Intake, above the lanes and outside the filter. It is the one thing on this
        board a reader trusts before anything else, and an empty Backlog is three
        different facts: the repository was read and had nothing open, the factory has
        never read it, or the factory tried and was refused. It is rendered whenever
        the factory serves a project, healthy or not, and it is not narrowed by the
        project filter — a filter that could hide a failing project could hide the
        reason there is nothing on the board.
      */}
      <IntakeSection intake={board.intake} />

      {totalCards === 0 ? (
        <EmptyState01
          reason="first-run"
          title="Nothing on the board yet"
          body="The factory is serving no work items. Intake fills the Backlog lane once a project is read."
        />
      ) : visibleCards === 0 ? (
        <EmptyState01
          reason="no-match"
          title="No work items for this project"
          body="Nothing waiting belongs to the project the board is narrowed to. Choose another project, or all of them."
        />
      ) : (
        <Kanban01 columns={lanes.map((lane) => toColumn(lane, refusals, onDecisionResult))} empty="Nothing in this lane." />
      )}
    </>
  );
}

/** The strip above the lanes: the container budget and the auto-merge mode. */
function BoardStrip({ board }: { board: BoardView }) {
  const full = board.budget.inUse >= board.budget.of;

  return (
    <div className="board__strip">
      <Status
        tone={full ? 'warning' : 'success'}
        label={`${board.budget.inUse} of ${board.budget.of} worker containers in use`}
      />
      <Badge variant={board.autoMerge ? 'success' : 'outline'}>
        {board.autoMerge ? 'auto-merge is on' : 'auto-merge is off'}
      </Badge>
    </div>
  );
}

/**
 * Intake, one row per served project: what the factory last did about that project's
 * open issues, in the three states a reader has to be able to tell apart.
 *
 * It re-decides nothing. The state's slug, the classification, the next-ask and the
 * row's whole sentence are the factory's own judgement, serialised by
 * `HowToReadIntake`; the renderer maps the slug to a Prism tone and draws the fields.
 * The section is drawn whenever the factory serves a project, and a project that
 * cannot be read is on it however the board is filtered, because an empty Backlog
 * while intake is broken must not read as nothing to do.
 */
function IntakeSection({ intake }: { intake: IntakeView }) {
  if (intake.rows.length === 0) {
    return null;
  }

  return (
    <section className="board__intake" data-intake="intake" data-intake-state={intake.status}>
      <h2 className="board__intake-title">Intake</h2>
      <p className="board__intake-summary" data-intake-summary="intake">
        {intake.summary}
      </p>

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Project</TableHead>
            <TableHead>Repository</TableHead>
            <TableHead>State</TableHead>
            <TableHead>Classification</TableHead>
            <TableHead>What it found</TableHead>
            <TableHead>Read again</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {intake.rows.map((row) => {
            const state = INTAKE_STATE[row.status] ?? { tone: 'neutral' as const, label: row.status };
            return (
              <TableRow
                key={row.project}
                data-intake-project={row.project}
                data-intake-state={row.status}
                data-repo-url={row.repoUrl}
                data-open-issues={row.openIssues ?? ''}
                data-classification={row.failure ?? ''}
                data-failures={row.failures}
                data-again={row.again}
              >
                <TableHead scope="row">{row.project}</TableHead>
                <TableCell>{row.repoUrl}</TableCell>
                <TableCell>
                  <Status tone={state.tone} label={state.label} size="sm" />
                </TableCell>
                <TableCell>{row.failure ?? ''}</TableCell>
                <TableCell>{row.says}</TableCell>
                <TableCell>{row.again}</TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </section>
  );
}

/**
 * The project filter, and it is a GET form rather than a set of links.
 *
 * It is a read, not a write: submitting it changes the query string and what the
 * board draws, and nothing else. Every project the factory serves is a submit
 * button with its own name, so one click narrows the board and one click widens it
 * again, and the project in force is marked with `aria-current`. There is no link
 * out of the app, because a filter is not a destination.
 */
function ProjectFilter({ board, project }: { board: BoardView; project: string | null }) {
  return (
    <form className="board__filter" method="get" aria-label="Narrow the board to one project">
      <span className="board__filter-label">Project</span>
      <Button
        type="submit"
        name="project"
        value=""
        variant="ghost"
        size="sm"
        aria-current={project === null ? 'true' : undefined}
      >
        All projects
      </Button>
      {board.projects.map((served) => (
        <Button
          key={served.name}
          type="submit"
          name="project"
          value={served.name}
          variant="ghost"
          size="sm"
          aria-current={project === served.name ? 'true' : undefined}
        >
          {served.name}
        </Button>
      ))}
    </form>
  );
}

/** One lane as a column, with its cards mapped to the Block's own card shape. */
function toColumn(
  lane: LaneView,
  refusals: Record<string, string>,
  onDecisionResult: (workItemId: string, result: DecisionResult) => void,
): KanbanColumn {
  return {
    id: lane.lane,
    label: lane.label,
    cards: lane.cards.map((card) => toCard(card, refusals, onDecisionResult)),
  };
}

/**
 * One work item as a card. The title, the Project as a tag, the lane's state and
 * its own words, the round count, and — where the factory offers any — the decisions a
 * reviewer can still make about it. Every one of them the factory's own judgement
 * rather than the renderer's: the offered set is `Decisions.OfferedIn`, serialised onto
 * the card, so a card in Review offers three, a parked card offers two, and no other
 * lane offers any.
 */
function toCard(
  card: CardView,
  refusals: Record<string, string>,
  onDecisionResult: (workItemId: string, result: DecisionResult) => void,
): KanbanCard {
  return {
    id: card.id,
    title: card.title,
    state: LANE_STATE[card.lane] ?? 'ready',
    stateLabel: card.laneLabel,
    tags: [{ id: card.project, label: card.project }],
    // The card is the way into the work item's own page: every round it has run, the change
    // each round left, and its decisions. The id travels in the query string because a
    // static export cannot emit an unbounded dynamic segment, and the label says what
    // activating the link does rather than leaving the title to imply it.
    href: `/work-items?item=${card.id}`,
    hrefLabel: 'Open the work item',
    body: (
      <div className="board__card-body">
        <span className="board__card-rounds">
          round {card.roundCount} of {card.roundCeiling}
        </span>
        {card.ending ? <span className="board__card-ending">{card.ending}</span> : null}
        {card.lane === 'Review' ? (
          <WorkspaceAction workItemId={card.id} workspace={card.workspace} />
        ) : null}
        {card.decisions.length > 0 ? (
          <DecisionForm
            workItemId={card.id}
            decisions={card.decisions}
            refusal={refusals[card.id] ?? null}
            onResult={(result) => onDecisionResult(card.id, result)}
          />
        ) : null}
      </div>
    ),
  };
}

/**
 * The review workspace, on one card in Review.
 *
 * A workspace is the reviewer's own copy of the round's tree, running code-server on
 * loopback, and this is the one control that opens it. It is a kind of write the board
 * did not have — it spawns a container — but it moves nothing, and the factory's answer
 * is the same either way: a link and the time left, or the factory's own words for why
 * it could not be opened. Nothing here ends in silence (#31, #47).
 *
 * The server's answer (`fromTheFactory`) is the truth and travels on the board's card,
 * so a card that already carries an open workspace renders its link on load. A press
 * posts to the endpoint and shows the result at once, tagged with the board value it
 * answered so the board's next poll — a fresh object every five seconds — supersedes it
 * without an effect: a press is a head start on the next read, not a second source of
 * truth.
 */
function WorkspaceAction({
  workItemId,
  workspace: fromTheFactory,
}: {
  workItemId: string;
  workspace: WorkspaceView | null;
}) {
  const [pressed, setPressed] = useState<{ answered: WorkspaceView | null; view: WorkspaceView } | null>(
    null,
  );
  const [pending, setPending] = useState(false);

  // The press's answer stands only while the board still carries the value it answered;
  // a poll hands a new object, the tag no longer matches, and the factory's answer is
  // shown again. No effect, no second render, no stale copy outliving the read it came
  // from.
  const workspace = pressed && pressed.answered === fromTheFactory ? pressed.view : fromTheFactory;

  const open = useCallback(() => {
    setPending(true);
    const answered = fromTheFactory;
    openWorkspace(workItemId)
      .then((view) => setPressed({ answered, view }))
      .catch((error: unknown) => {
        setPressed({
          answered,
          view: {
            active: false,
            url: null,
            remainingSeconds: null,
            error: error instanceof Error ? error.message : 'the factory could not be reached',
          },
        });
      })
      .finally(() => setPending(false));
  }, [workItemId, fromTheFactory]);

  if (workspace?.active && workspace.url) {
    return (
      <span className="board__workspace">
        <CtaLink href={workspace.url} newTab variant="outline" size="sm">
          {workspace.url}
        </CtaLink>
        {workspace.remainingSeconds !== null ? (
          <span className="board__workspace-remaining">{remainingLabel(workspace.remainingSeconds)}</span>
        ) : null}
      </span>
    );
  }

  return (
    <span className="board__workspace">
      <Button type="button" variant="outline" size="sm" onClick={open} disabled={pending}>
        {pending ? 'Opening the workspace' : 'Open workspace'}
      </Button>
      {/*
        A workspace that cannot be opened is usually because the round left no tree to
        open, and the thing a reviewer wants next is the work item's own page — its rounds,
        the change each left, and its decisions. The factory's own words for the refusal
        stay beside the link rather than being replaced by it.
      */}
      {workspace?.error ? (
        <Status tone="destructive" label={workspace.error} />
      ) : null}
      {workspace?.error ? (
        <CtaLink href={`/work-items?item=${workItemId}`} variant="outline" size="sm">
          See the work item
        </CtaLink>
      ) : null}
    </span>
  );
}

/**
 * The time left on a workspace, in a reviewer's words. The factory sends whole seconds;
 * this says them in the largest two units that matter, because "3h 59m remaining" is the
 * answer to "how long have I got" and "14390 seconds" is not.
 */
function remainingLabel(seconds: number): string {
  const safe = Math.max(0, Math.floor(seconds));
  const hours = Math.floor(safe / 3600);
  const minutes = Math.floor((safe % 3600) / 60);
  if (hours > 0) return `${hours}h ${minutes}m remaining`;
  if (minutes > 0) return `${minutes}m remaining`;
  return `${safe}s remaining`;
}

/** The board before the factory has answered: the shape of it, in Prism skeletons. */
function BoardLoading() {
  return (
    <div className="board__loading">
      <Status tone="info" label="Reading the factory" />
      <div className="board__loading-columns" aria-hidden="true">
        {Array.from({ length: 4 }, (_, index) => (
          <div className="board__loading-column" key={index}>
            <Skeleton className="board__loading-heading" />
            <Skeleton className="board__loading-card" />
            <Skeleton className="board__loading-card" />
          </div>
        ))}
      </div>
    </div>
  );
}
