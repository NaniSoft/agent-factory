'use client';

import { useMemo } from 'react';

import { Dashboard01 } from '@nanisoft/prism-ui/blocks/dashboard-01';
import { EmptyState01 } from '@nanisoft/prism-ui/blocks/empty-state-01';
import { Badge } from '@nanisoft/prism-ui/components/badge';
import { Button } from '@nanisoft/prism-ui/components/button';
import { CtaLink } from '@nanisoft/prism-ui/components/cta-link';
import { Status } from '@nanisoft/prism-ui/components/status';
import type { MetricSpec } from '@nanisoft/prism-ui/spec';

import { PageHeader } from '@/components/page-header';
import { ErrorCard, LoadingCard } from '@/components/reading-state';
import { RunsTable } from '@/components/runs-table';
import { formatWhen } from '@/lib/format';
import type { Fleet, RunSummary } from '@/lib/types';
import { useApi } from '@/lib/use-api';

/**
 * The fleet at a glance: what is running, what is waiting, and whether the
 * engine that does the work is up. Every figure and every word is the API's
 * own reading; the page draws it and decides nothing.
 */
export default function FleetPage() {
  const fleet = useApi<Fleet>('/api/fleet', 5000);
  const runs = useApi<RunSummary[]>('/api/runs', 5000);

  const refresh = () => {
    fleet.refresh();
    runs.refresh();
  };

  if (fleet.reading.phase === 'error') {
    return (
      <div className="app-page">
        <PageHeader
          title="The fleet"
          description="The engine, the intake and every run the factory is handling."
        />
        <ErrorCard
          title="The fleet could not be read"
          message={fleet.reading.message}
          onRetry={refresh}
        />
      </div>
    );
  }

  if (fleet.reading.phase === 'loading' || runs.reading.phase === 'loading') {
    return (
      <div className="app-page">
        <PageHeader
          title="The fleet"
          description="The engine, the intake and every run the factory is handling."
        />
        <LoadingCard label="Reading the fleet and its runs" />
      </div>
    );
  }

  if (runs.reading.phase === 'error') {
    return (
      <div className="app-page">
        <PageHeader
          title="The fleet"
          description="The engine, the intake and every run the factory is handling."
        />
        <ErrorCard
          title="The runs could not be read"
          message={runs.reading.message}
          onRetry={refresh}
        />
      </div>
    );
  }

  return (
    <div className="app-page app-page--dashboard">
      <FleetDashboard
        fleet={fleet.reading.data}
        runs={runs.reading.data}
        onRefresh={refresh}
      />
    </div>
  );
}

function FleetDashboard({
  fleet,
  runs,
  onRefresh,
}: {
  fleet: Fleet;
  runs: readonly RunSummary[];
  onRefresh: () => void;
}) {
  const stats = useMemo<MetricSpec[]>(
    () => [
      {
        key: 'succeeded',
        label: 'Succeeded',
        value: String(fleet.runs.succeeded),
        hint: `of ${fleet.runs.total} runs`,
      },
      {
        key: 'failed',
        label: 'Failed',
        value: String(fleet.runs.failed),
        hint: fleet.runs.failed === 0 ? 'nothing failed' : 'read the run log',
      },
      {
        key: 'working',
        label: 'Working',
        value: String(fleet.runs.working),
        hint: fleet.runs.working === 0 ? 'the queue is quiet' : 'a session is in flight',
      },
      {
        key: 'queued',
        label: 'Queued',
        value: String(fleet.runs.queued),
        hint: fleet.paused ? 'dequeue is paused' : 'waiting for a slot',
      },
    ],
    [fleet],
  );

  return (
    <Dashboard01
      className="app-dashboard"
      // The page header above is the screen's one `h1`, and the dashboard carries no
      // section title of its own, so its panels are the next level under the page
      // rather than a step under a heading that is not drawn. `headingLevel` is the
      // level of the section title when there is one; here it is what the panel
      // titles derive from.
      headingLevel="h1"
      header={
        <PageHeader
          title="The fleet"
          description="GitHub issues in, reviewed pull requests out. This is what the factory is doing right now and what it has already done."
          actions={
            <>
              <CtaLink href="/targets" variant="outline" size="sm">
                Manage targets
              </CtaLink>
              <Button type="button" size="sm" variant="outline" onClick={onRefresh}>
                Refresh
              </Button>
            </>
          }
        />
      }
      metrics={stats}
      panels={[
        {
          id: 'runs',
          title: 'Runs',
          span: 'full',
          toolbar: (
            <span className="app-small app-muted">
              polled every 5 s · {formatWhen(fleet.now)}
            </span>
          ),
          children: (
            <RunsTable
              runs={runs}
              empty={
                <EmptyState01
                  reason="first-run"
                  title="No runs yet"
                  body={`Label an issue ${fleet.optInLabel} on an enrolled target and the factory will pick it up on its next poll.`}
                />
              }
            />
          ),
        },
        {
          id: 'engine',
          title: 'Engine',
          span: 'half',
          children: <EnginePanel fleet={fleet} />,
        },
        {
          id: 'intake',
          title: 'Intake',
          span: 'half',
          children: <IntakePanel fleet={fleet} />,
        },
      ]}
    />
  );
}

function EnginePanel({ fleet }: { fleet: Fleet }) {
  return (
    <div className="app-status-card">
      <div className="app-status-card__row">
        <span className="app-status-card__label">opencode serve</span>
        <Status
          tone={fleet.engine.up ? 'success' : 'destructive'}
          label={fleet.engine.up ? 'up' : 'down'}
          size="sm"
        />
      </div>
      <div className="app-status-card__row">
        <span className="app-status-card__label">Loopback port</span>
        <span className="app-status-card__value app-mono">{fleet.engine.port}</span>
      </div>
      <div className="app-status-card__row">
        <span className="app-status-card__label">Fleet</span>
        <Badge variant={fleet.paused ? 'warning' : 'success'}>
          {fleet.paused ? 'paused' : 'running'}
        </Badge>
      </div>
      <p className="app-hint">
        A down engine leaves the board readable and the queue waiting. Pause the fleet from the
        bar; resume it the same way.
      </p>
    </div>
  );
}

function IntakePanel({ fleet }: { fleet: Fleet }) {
  return (
    <div className="app-status-card">
      <div className="app-status-card__row">
        <span className="app-status-card__label">Opt-in label</span>
        <code className="app-code">{fleet.optInLabel}</code>
      </div>
      <div className="app-status-card__row">
        <span className="app-status-card__label">Enrolled targets</span>
        <span className="app-status-card__value">{fleet.targetCount}</span>
      </div>
      <div className="app-status-card__row">
        <span className="app-status-card__label">Poll interval</span>
        <span className="app-status-card__value">{fleet.pollSeconds} s</span>
      </div>
      <div className="app-status-card__row">
        <span className="app-status-card__label">Manage</span>
        <CtaLink href="/targets" variant="ghost" size="sm">
          Open targets
        </CtaLink>
      </div>
    </div>
  );
}
