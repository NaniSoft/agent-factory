'use client';

import { useCallback, useEffect, useState } from 'react';

import { Badge } from '@nanisoft/prism-ui/components/badge';
import { Button } from '@nanisoft/prism-ui/components/button';
import { Card, CardContent, CardHeader, CardTitle } from '@nanisoft/prism-ui/components/card';
import { Metric } from '@nanisoft/prism-ui/components/metric';
import { Status } from '@nanisoft/prism-ui/components/status';

import { fetchBoard, type BoardView } from '@/lib/board';

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
 * loopback API. It re-decides nothing — it renders the budget and the auto-merge
 * mode exactly as the factory serialised them, through Prism items.
 */
export default function BoardPage() {
  const [reading, setReading] = useState<Reading>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();

    fetchBoard(controller.signal)
      .then((board) => setReading({ phase: 'ready', board }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setReading({
          phase: 'error',
          message: error instanceof Error ? error.message : 'the factory could not be reached',
        });
      });

    return () => controller.abort();
  }, [attempt]);

  // The loading state is the initial state and a retry's, and it is set from the
  // event that starts the read rather than from inside the effect: an effect that
  // synchronously set state would start a second render for every fetch, and the
  // state a reader sees between two reads is the same either way.
  const retry = useCallback(() => {
    setReading({ phase: 'loading' });
    setAttempt((count) => count + 1);
  }, []);

  return (
    <main className="page">
      <header className="page__header">
        <h1 className="page__title">The factory board</h1>
        <p className="page__lede">
          What the factory is doing right now: how many worker containers it is inside, and whether
          silence can merge a change.
        </p>
      </header>

      {reading.phase === 'loading' ? (
        <Status tone="info" label="Reading the factory" />
      ) : reading.phase === 'error' ? (
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
      ) : (
        <div className="page__panels">
          <Card>
            <CardHeader>
              <CardTitle>Worker containers</CardTitle>
            </CardHeader>
            <CardContent>
              <Metric
                value={reading.board.budget.inUse}
                unit={`of ${reading.board.budget.of}`}
                label="worker containers in use"
              />
              <Status
                tone={
                  reading.board.budget.inUse >= reading.board.budget.of ? 'warning' : 'success'
                }
                label={`${reading.board.budget.inUse} of ${reading.board.budget.of} worker containers in use`}
              />
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>Auto-merge</CardTitle>
            </CardHeader>
            <CardContent>
              <Badge variant={reading.board.autoMerge ? 'success' : 'outline'}>
                {reading.board.autoMerge ? 'auto-merge is on' : 'auto-merge is off'}
              </Badge>
              <p className="page__hint">
                {reading.board.autoMerge
                  ? 'A work item left in Review past the threshold is merged without a reviewer.'
                  : 'Nothing merges without a reviewer.'}
              </p>
            </CardContent>
          </Card>
        </div>
      )}
    </main>
  );
}
