'use client';

import { Suspense, useState } from 'react';
import { useSearchParams } from 'next/navigation';

import { EmptyState01 } from '@nanisoft/prism-ui/blocks/empty-state-01';
import { Alert, AlertDescription, AlertTitle } from '@nanisoft/prism-ui/components/alert';
import { Badge } from '@nanisoft/prism-ui/components/badge';
import { Button } from '@nanisoft/prism-ui/components/button';
import { Card, CardContent, CardHeader, CardTitle } from '@nanisoft/prism-ui/components/card';
import { CtaLink } from '@nanisoft/prism-ui/components/cta-link';
import { LiveRegion } from '@nanisoft/prism-ui/components/live-region';
import { Status } from '@nanisoft/prism-ui/components/status';
import { Timeline, type TimelineEntry } from '@nanisoft/prism-ui/components/timeline';

import { PageHeader } from '@/components/page-header';
import { ErrorCard, LoadingCard } from '@/components/reading-state';
import { RunsTable } from '@/components/runs-table';
import {
  formatClock,
  formatCost,
  formatDuration,
  formatTokens,
  formatWhen,
  kindLabel,
  lastSegment,
  stateVariant,
} from '@/lib/format';
import type { RunDetail, RunSummary, TranscriptView } from '@/lib/types';
import { useApi, type Reading } from '@/lib/use-api';

/**
 * The run surface. With `?run=<id>` it is one run's own page; without it, the
 * history in a table. The static export cannot emit a dynamic segment for an
 * unbounded set of runs, so the id travels in the query string.
 */
export default function RunsPage() {
  return (
    <Suspense fallback={<RunsFallback />}>
      <RunsBody />
    </Suspense>
  );
}

function RunsFallback() {
  return (
    <div className="app-page">
      <PageHeader
        title="Runs"
        description="Every issue the factory has picked up, and what became of it."
      />
      <LoadingCard label="Reading the run history" />
    </div>
  );
}

function RunsBody() {
  const runId = useSearchParams().get('run');
  return runId === null ? <RunIndex /> : <RunDetailView key={runId} runId={runId} />;
}

function RunIndex() {
  const runs = useApi<RunSummary[]>('/api/runs', 5000);

  return (
    <div className="app-page app-page--wide">
      <PageHeader
        title="Runs"
        description="Every issue the factory has picked up, what pipeline it walked, and where it landed."
        actions={<CtaLink href="/" variant="outline" size="sm">Back to the fleet</CtaLink>}
      />
      {runs.reading.phase === 'loading' ? (
        <LoadingCard label="Reading the run history" />
      ) : runs.reading.phase === 'error' ? (
        <ErrorCard
          title="The run history could not be read"
          message={runs.reading.message}
          onRetry={runs.refresh}
        />
      ) : (
        <Card>
          <CardHeader>
            <CardTitle as="h2">History</CardTitle>
          </CardHeader>
          <CardContent>
            <RunsTable
              runs={runs.reading.data}
              empty={
                <EmptyState01
                  reason="first-run"
                  title="No runs yet"
                  body="The fleet creates a run when an enrolled target carries the opt-in label on an issue."
                />
              }
            />
          </CardContent>
        </Card>
      )}
    </div>
  );
}

function RunDetailView({ runId }: { runId: string }) {
  const detail = useApi<RunDetail>(`/api/runs/${encodeURIComponent(runId)}`, 2000);
  const [phase, setPhase] = useState<number | null>(null);
  const transcript = useApi<TranscriptView>(
    `/api/runs/${encodeURIComponent(runId)}/transcript${phase === null ? '' : `?phase=${phase}`}`,
    2000,
  );

  if (detail.reading.phase === 'loading') {
    return (
      <div className="app-page app-page--wide">
        <LoadingCard label={`Reading ${runId}`} />
      </div>
    );
  }

  if (detail.reading.phase === 'error') {
    return (
      <div className="app-page app-page--wide">
        <ErrorCard
          title="The run could not be read"
          message={detail.reading.message}
          onRetry={detail.refresh}
        />
      </div>
    );
  }

  const run = detail.reading.data;
  const summary = run.run;
  const selectedPhase =
    transcript.reading.phase === 'ready'
      ? transcript.reading.data.selectedPhaseIndex
      : phase ?? Math.max(0, run.phases.length - 1);
  const steps: TimelineEntry[] = run.phases.map((entry, index) => ({
    id: entry.sessionId ?? `${entry.name}-${index}`,
    title: `${index + 1}. ${entry.name}`,
    duration:
      entry.finishedAt === null
        ? undefined
        : new Date(entry.finishedAt).getTime() - new Date(entry.startedAt).getTime(),
    state: entry.finishedAt === null ? 'running' : 'done',
    children: (
      <div className="app-stack app-stack--sm">
        <span className="app-small app-muted">
          {entry.profileName === null
            ? 'engine default'
            : `${entry.flavour ?? 'pinned'} · ${entry.profileName} · ${lastSegment(entry.modelId)}`}
        </span>
        <span className="app-small app-muted">
          skills: {entry.skills.length === 0 ? 'none' : entry.skills.join(', ')}
        </span>
        <span className="app-small app-muted app-mono">
          {entry.sessionId ?? 'no session'} · {formatDuration(entry.startedAt, entry.finishedAt)} ·{' '}
          {formatTokens(entry.tokensIn, entry.tokensOut)} · {formatCost(entry.cost)}
        </span>
      </div>
    ),
  }));

  const phaseTimeline = (
    <Timeline
      entries={steps}
      label="The phases this run walked"
      empty="The pipeline has not started a phase yet."
    />
  );

  return (
    <div className="app-page app-page--wide">
      <div className="app-run">
        <div className="app-run__head">
          <PageHeader
            title={summary.issueTitle}
            description={
              <span className="app-run__badges">
                <Badge variant={stateVariant(summary.state)}>{summary.state}</Badge>
                <Badge variant="outline">{kindLabel(summary.kind)}</Badge>
                {summary.live ? <Status tone="info" label="live" size="sm" /> : null}
                {summary.triageVerdict === null ? null : (
                  <Badge variant="secondary">triage: {summary.triageVerdict}</Badge>
                )}
              </span>
            }
            actions={
              <CtaLink href="/" variant="outline" size="sm">
                Back to the fleet
              </CtaLink>
            }
          />

          <div className="app-run__meta">
            <span className="app-run__meta-item">
              issue{' '}
              <a className="app-link" href={summary.issueUrl} target="_blank" rel="noreferrer">
                #{summary.issueNumber}
              </a>
            </span>
            <span className="app-run__meta-item">
              target{' '}
              <a
                className="app-link app-mono"
                href={`https://github.com/${summary.repo}`}
                target="_blank"
                rel="noreferrer"
              >
                {summary.repo}
              </a>
            </span>
            <span className="app-run__meta-item">
              run <code className="app-code">{summary.runId}</code>
            </span>
            <span className="app-run__meta-item">attempt {summary.attempt}</span>
            <span className="app-run__meta-item">
              branch <code className="app-code">{summary.branch ?? '—'}</code>
            </span>
            {summary.pipelineName === null ? null : (
              <span className="app-run__meta-item">pipeline {summary.pipelineName}</span>
            )}
            {summary.prUrl === null ? null : (
              <a className="app-link" href={summary.prUrl} target="_blank" rel="noreferrer">
                pull request
              </a>
            )}
            <span className="app-run__meta-item">
              started {formatWhen(summary.createdAt)}
              {summary.finishedAt === null ? '' : ` · finished ${formatWhen(summary.finishedAt)}`}
            </span>
          </div>
        </div>

        {summary.error === null ? null : (
          <Alert variant="destructive">
            <AlertTitle>Last error</AlertTitle>
            <AlertDescription>{summary.error}</AlertDescription>
          </Alert>
        )}

        {run.issueBody.length === 0 ? null : (
          <details className="app-details">
            <summary>Issue body</summary>
            <pre className="app-pre">{run.issueBody}</pre>
          </details>
        )}

        {summary.triageSummary === null ? null : (
          <Alert>
            <AlertTitle>Triage</AlertTitle>
            <AlertDescription>{summary.triageSummary}</AlertDescription>
          </Alert>
        )}

        {summary.routedIssueUrl === null ? null : (
          <Alert>
            <AlertTitle>Routed</AlertTitle>
            <AlertDescription>
              This ticket became a cross-repo parent at{' '}
              <a className="app-link" href={summary.routedIssueUrl} target="_blank" rel="noreferrer">
                the org inbox issue
              </a>
              .
            </AlertDescription>
          </Alert>
        )}

        {summary.parentRunId === null ? null : (
          <p className="app-small app-muted">
            Child {summary.childOrder + 1} of{' '}
            <CtaLink href={`/runs?run=${encodeURIComponent(summary.parentRunId)}`} variant="ghost" size="sm">
              {summary.parentRunId}
            </CtaLink>
            .
          </p>
        )}

        {run.phases.length === 0 ? null : (
          <Card>
            <CardHeader>
              <CardTitle as="h2">Phases</CardTitle>
            </CardHeader>
            <CardContent>
              {summary.live ? (
                <LiveRegion busy label="The phases this run walked">
                  {phaseTimeline}
                </LiveRegion>
              ) : (
                phaseTimeline
              )}
            </CardContent>
          </Card>
        )}

        <div className="app-grid-2">
          <Card>
            <CardHeader>
              <CardTitle as="h2">Run log</CardTitle>
            </CardHeader>
            <CardContent>
              <pre className="app-pre">{run.logTail ?? '(no log lines yet)'}</pre>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle as="h2">Session transcript</CardTitle>
            </CardHeader>
            <CardContent>
              <div className="app-stack">
                {run.phases.length > 1 ? (
                  <div className="app-row" role="group" aria-label="Choose a phase transcript">
                    {run.phases.map((entry, index) => (
                      <Button
                        key={`${entry.name}-${index}`}
                        type="button"
                        size="sm"
                        variant={index === selectedPhase ? 'default' : 'outline'}
                        aria-pressed={index === selectedPhase}
                        onClick={() => setPhase(index)}
                      >
                        {entry.name}
                      </Button>
                    ))}
                  </div>
                ) : null}
                <TranscriptPanel transcript={transcript} />
              </div>
            </CardContent>
          </Card>
        </div>

        {run.session === null ? null : (
          <p className="app-small app-muted">
            engine session <code className="app-code">{run.session.id}</code>
            {run.session.agent.length === 0 ? '' : ` · ${run.session.agent}`}
            {run.session.modelId.length === 0 ? '' : ` / ${run.session.modelId}`}
            {` · ${formatCost(run.session.cost)} · ${formatTokens(run.session.tokensIn, run.session.tokensOut)} tokens`}
            {` · updated ${formatClock(run.session.updated)}`}
          </p>
        )}

        {run.agentSummary === null || run.agentSummary.length === 0 ? null : (
          <Card>
            <CardHeader>
              <CardTitle as="h2">Agent summary</CardTitle>
            </CardHeader>
            <CardContent>
              <pre className="app-summary">{run.agentSummary}</pre>
            </CardContent>
          </Card>
        )}

        {run.children.length === 0 ? null : (
          <Card>
            <CardHeader>
              <CardTitle as="h2">Children</CardTitle>
            </CardHeader>
            <CardContent>
              <RunsTable runs={run.children} empty={<p className="app-muted">No children.</p>} />
            </CardContent>
          </Card>
        )}

        <CtaLink href="/" variant="outline" size="sm">
          Back to the fleet
        </CtaLink>
      </div>
    </div>
  );
}

function TranscriptPanel({ transcript }: { transcript: { reading: Reading<TranscriptView> } }) {
  if (transcript.reading.phase === 'loading') {
    return <Status tone="info" label="Reading the transcript" size="sm" />;
  }
  if (transcript.reading.phase === 'error') {
    return (
      <Alert variant="destructive">
        <AlertTitle>The transcript could not be read</AlertTitle>
        <AlertDescription>{transcript.reading.message}</AlertDescription>
      </Alert>
    );
  }

  const lines = transcript.reading.data.transcript;
  if (lines.length === 0) {
    return <p className="app-muted">No transcript lines are available for this phase.</p>;
  }

  return (
    <div className="app-transcript">
      {lines.map((line, index) => (
        <div className="app-transcript__line" key={`${line.time}-${index}`}>
          <div className="app-transcript__head">
            <Badge variant={line.role === 'user' ? 'secondary' : 'outline'}>{line.role}</Badge>
            <span className="app-small app-muted">{formatClock(line.time)}</span>
          </div>
          <p className="app-transcript__text">{line.text}</p>
        </div>
      ))}
    </div>
  );
}
