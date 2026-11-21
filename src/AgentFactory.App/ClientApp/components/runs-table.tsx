import type { ReactNode } from 'react';

import { Badge } from '@nanisoft/prism-ui/components/badge';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@nanisoft/prism-ui/components/table';

import { formatWhen, kindLabel, stateVariant } from '@/lib/format';
import type { RunSummary } from '@/lib/types';

/**
 * The run history, in one table.
 *
 * **Seven columns, because eight did not fit the column the board gives it.** An
 * attempt count was a column of its own and was a `1` on almost every row; it is
 * now the quiet line under the run id, drawn only when a run was tried more than
 * once, which is the one case a reader has to know about. The width that freed
 * belongs to `Updated`, a fact a reader scanning an operations table looks for
 * first and the one column that used to sit outside the frame.
 *
 * A run id opens the run's own page; the repo, the issue and the pull request are
 * the three places a reader leaves the board for, and each is a native anchor so
 * the browser's own affordances work.
 */
export function RunsTable({ runs, empty }: { runs: readonly RunSummary[]; empty: ReactNode }) {
  if (runs.length === 0) {
    return <>{empty}</>;
  }

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>Run</TableHead>
          <TableHead>Issue</TableHead>
          <TableHead>State</TableHead>
          <TableHead>Branch</TableHead>
          <TableHead>PR</TableHead>
          <TableHead>Updated</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {runs.map((run) => (
          <TableRow key={run.runId}>
            <TableHead scope="row">
              <a className="app-link app-mono" href={`/runs?run=${encodeURIComponent(run.runId)}`}>
                {run.runId}
              </a>
              {run.kind === 'normal' && run.attempt < 2 ? null : (
                <div className="app-small app-muted">
                  {[run.kind === 'normal' ? null : kindLabel(run.kind), run.attempt > 1 ? `attempt ${run.attempt}` : null]
                    .filter(Boolean)
                    .join(' · ')}
                </div>
              )}
            </TableHead>
            <TableCell>
              <div className="app-table-target">
                <span>
                  <a
                    className="app-link"
                    href={run.issueUrl}
                    target="_blank"
                    rel="noreferrer"
                    title={run.issueTitle}
                  >
                    #{run.issueNumber}
                  </a>{' '}
                  <span>{run.issueTitle}</span>
                </span>
                <a
                  className="app-link app-mono app-small"
                  href={`https://github.com/${run.repo}`}
                  target="_blank"
                  rel="noreferrer"
                >
                  {run.repo}
                </a>
              </div>
            </TableCell>
            <TableCell>
              <Badge variant={stateVariant(run.state)}>{run.state}</Badge>
            </TableCell>
            <TableCell>
              <code className="app-code">{run.branch ?? '—'}</code>
            </TableCell>
            <TableCell>
              {run.prUrl === null ? (
                <span className="app-muted">—</span>
              ) : (
                <a className="app-link" href={run.prUrl} target="_blank" rel="noreferrer">
                  PR
                </a>
              )}
            </TableCell>
            <TableCell className="app-muted app-small app-nowrap">
              {formatWhen(run.finishedAt ?? run.createdAt)}
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
