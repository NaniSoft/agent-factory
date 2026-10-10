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
import { Skeleton } from '@nanisoft/prism-ui/components/skeleton';
import { Status } from '@nanisoft/prism-ui/components/status';

import { fetchBoard, type BoardView, type CardView, type LaneView } from '@/lib/board';

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
    <main className="board">
      <header className="board__header">
        <h1 className="board__title">The factory board</h1>
        <p className="board__lede">
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
          <div className="board__error">
            <Status tone="destructive" label={reading.message} />
            <Button variant="outline" onClick={retry}>
              Try again
            </Button>
          </div>
        </CardContent>
      </Card>
    );
  }

  return <BoardReady board={reading.board} project={project} />;
}

/** The board once the factory has answered: the strip, the filter and the lanes. */
function BoardReady({ board, project }: { board: BoardView; project: string | null }) {
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
        <Kanban01 columns={lanes.map(toColumn)} empty="Nothing in this lane." />
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
function toColumn(lane: LaneView): KanbanColumn {
  return {
    id: lane.lane,
    label: lane.label,
    cards: lane.cards.map(toCard),
  };
}

/**
 * One work item as a card. The title, the Project as a tag, the lane's state and
 * its own words, and the round count — every one of them the factory's own
 * judgement rather than the renderer's.
 */
function toCard(card: CardView): KanbanCard {
  return {
    id: card.id,
    title: card.title,
    state: LANE_STATE[card.lane] ?? 'ready',
    stateLabel: card.laneLabel,
    tags: [{ id: card.project, label: card.project }],
    body: (
      <span className="board__card-body">
        <span className="board__card-rounds">
          round {card.roundCount} of {card.roundCeiling}
        </span>
        {card.ending ? <span className="board__card-ending">{card.ending}</span> : null}
      </span>
    ),
  };
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
