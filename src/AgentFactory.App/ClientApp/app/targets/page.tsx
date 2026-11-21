'use client';

import { useEffect, useState } from 'react';

import { Alert, AlertDescription, AlertTitle } from '@nanisoft/prism-ui/components/alert';
import { Badge } from '@nanisoft/prism-ui/components/badge';
import { Button } from '@nanisoft/prism-ui/components/button';
import { Card, CardContent, CardHeader, CardTitle } from '@nanisoft/prism-ui/components/card';
import {
  Drawer,
  DrawerBody,
  DrawerContent,
  DrawerFooter,
  DrawerHeader,
  DrawerTitle,
} from '@nanisoft/prism-ui/components/drawer';
import { Input } from '@nanisoft/prism-ui/components/input';
import { NativeSelect } from '@nanisoft/prism-ui/components/native-select';
import { Switch } from '@nanisoft/prism-ui/components/switch';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@nanisoft/prism-ui/components/table';

import { PageHeader } from '@/components/page-header';
import { ErrorCard, LoadingCard } from '@/components/reading-state';
import { api } from '@/lib/api';
import { formatAgo, messageOf } from '@/lib/format';
import type { GithubToken, GithubVerifyResult, Org, OrgScan, Target, TargetsView } from '@/lib/types';
import { useApi } from '@/lib/use-api';

/**
 * The fleet's enrollment surface: which repos and orgs the factory works, how
 * each one admits an issue, which pipeline it walks, and whether it is on.
 * Rows are the summary; the settings drawer is the edit.
 */
export default function TargetsPage() {
  const targets = useApi<TargetsView>('/api/targets', 15000);

  return (
    <div className="app-page app-page--wide">
      <PageHeader
        title="Targets"
        description="The repos and orgs the fleet works. Enrollment probes with gh and fires the first scan immediately; org rows materialize into per-repo targets at discovery."
        actions={
          <Button type="button" size="sm" variant="outline" onClick={targets.refresh}>
            Refresh
          </Button>
        }
      />
      {targets.reading.phase === 'loading' ? (
        <LoadingCard label="Reading the enrolled targets" />
      ) : targets.reading.phase === 'error' ? (
        <ErrorCard
          title="The targets could not be read"
          message={targets.reading.message}
          onRetry={targets.refresh}
        />
      ) : (
        <TargetsBody data={targets.reading.data} reload={targets.refresh} />
      )}
    </div>
  );
}

function TargetsBody({ data, reload }: { data: TargetsView; reload: () => void }) {
  const [error, setError] = useState<string | null>(null);
  const [selection, setSelection] = useState<
    { kind: 'target'; value: Target } | { kind: 'org'; value: Org } | null
  >(null);

  const defaultName =
    data.pipelines.find((pipeline) => pipeline.id === data.defaultPipelineId)?.name ?? '(none)';

  const pipelineLabel = (pipelineId: string | null): string =>
    data.pipelines.find((pipeline) => pipeline.id === pipelineId)?.name ??
    `(fleet default: ${defaultName})`;

  const queuedForOrg = (org: string): number =>
    data.targets
      .filter((target) => target.org === org)
      .reduce((total, target) => total + (data.queued[target.repo] ?? 0), 0);

  const toggleTarget = (target: Target, checked: boolean) => {
    setError(null);
    api
      .updateTarget({ ...target, enabled: checked })
      .then(reload)
      .catch((failure: unknown) => setError(messageOf(failure)));
  };

  const toggleOrg = (org: Org, checked: boolean) => {
    setError(null);
    api
      .updateOrg({ ...org, enabled: checked })
      .then(reload)
      .catch((failure: unknown) => setError(messageOf(failure)));
  };

  return (
    <div className="app-stack">
      <GithubCard />
      <EnrollForm reload={reload} />

      {error === null ? null : (
        <Alert variant="destructive">
          <AlertTitle>The change was rejected</AlertTitle>
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      )}

      <Card>
        <CardHeader>
          <CardTitle as="h2">Enrolled targets</CardTitle>
        </CardHeader>
        <CardContent>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Kind</TableHead>
                <TableHead>Target</TableHead>
                <TableHead>Intake</TableHead>
                <TableHead>Pipeline</TableHead>
                <TableHead>Last scan</TableHead>
                <TableHead>Queued</TableHead>
                <TableHead>Enabled</TableHead>
                <TableHead>
                  <span className="app-visually-hidden">Settings</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {data.orgs.map((org) => {
                const scan = data.orgScans[org.org];
                return (
                  <TableRow key={`org-${org.org}`}>
                    <TableCell>
                      <Badge variant="outline">org</Badge>
                    </TableCell>
                    <TableCell>
                      <div className="app-table-target">
                        <a
                          className="app-link app-mono"
                          href={`https://github.com/${org.org}`}
                          target="_blank"
                          rel="noreferrer"
                        >
                          {org.org}
                        </a>
                        <span className="app-org-detail">{scanSummary(scan, data.targets)}</span>
                      </div>
                    </TableCell>
                    <TableCell>
                      <Badge variant="outline">{org.intake}</Badge>
                    </TableCell>
                    <TableCell className="app-muted app-small">
                      {pipelineLabel(org.pipelineId)}
                    </TableCell>
                    <TableCell className="app-muted app-small">
                      {scan?.listedAt === null || scan === undefined ? 'first scan running…' : formatAgo(scan.listedAt)}
                    </TableCell>
                    <TableCell className="app-mono">{queuedForOrg(org.org)}</TableCell>
                    <TableCell>
                      <Switch
                        checked={org.enabled}
                        aria-label={`Enable org ${org.org}`}
                        onCheckedChange={(checked) => toggleOrg(org, checked)}
                      />
                    </TableCell>
                    <TableCell>
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        onClick={() => setSelection({ kind: 'org', value: { ...org } })}
                      >
                        Settings
                      </Button>
                    </TableCell>
                  </TableRow>
                );
              })}
              {data.targets.map((target) => {
                const poll = data.lastPoll[target.repo];
                return (
                  <TableRow key={`target-${target.repo}`}>
                    <TableCell>
                      <Badge variant="outline">repo</Badge>
                    </TableCell>
                    <TableCell>
                      <div className="app-table-target">
                        <a
                          className="app-link app-mono"
                          href={`https://github.com/${target.repo}`}
                          target="_blank"
                          rel="noreferrer"
                        >
                          {target.repo}
                        </a>
                        {target.org === null ? null : (
                          <span className="app-org-detail">via {target.org}</span>
                        )}
                        {poll?.note === null || poll === undefined ? null : (
                          <span className="app-error app-org-detail">poll error — {poll.note}</span>
                        )}
                      </div>
                    </TableCell>
                    <TableCell>
                      <Badge variant="outline">{target.intake}</Badge>
                    </TableCell>
                    <TableCell className="app-muted app-small">
                      {pipelineLabel(target.pipelineId)}
                    </TableCell>
                    <TableCell className="app-muted app-small">
                      {poll === undefined ? 'first scan running…' : formatAgo(poll.at)}
                    </TableCell>
                    <TableCell className="app-mono">{data.queued[target.repo] ?? 0}</TableCell>
                    <TableCell>
                      <Switch
                        checked={target.enabled}
                        aria-label={`Enable target ${target.repo}`}
                        onCheckedChange={(checked) => toggleTarget(target, checked)}
                      />
                    </TableCell>
                    <TableCell>
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        onClick={() => setSelection({ kind: 'target', value: { ...target } })}
                      >
                        Settings
                      </Button>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
          {data.orgs.length === 0 && data.targets.length === 0 ? (
            <p className="app-muted">No targets yet. Enroll a repo or org above.</p>
          ) : null}
        </CardContent>
      </Card>

      <SettingsDrawer
        selection={selection}
        pipelines={data.pipelines}
        defaultName={defaultName}
        onClose={() => setSelection(null)}
        onSaved={() => {
          setSelection(null);
          reload();
        }}
        onUnenrolled={() => {
          setSelection(null);
          reload();
        }}
      />
    </div>
  );
}

function GithubCard() {
  const gh = useApi<GithubToken>('/api/fleet/github');
  const [token, setToken] = useState('');
  const [show, setShow] = useState(false);
  const [busy, setBusy] = useState(false);
  const [verifying, setVerifying] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [result, setResult] = useState<GithubVerifyResult | null>(null);

  const save = async () => {
    setBusy(true);
    setError(null);
    setNote(null);
    try {
      await api.saveGithubToken(token.trim().length === 0 ? null : token.trim());
      setToken('');
      setNote('Saved — the next gh call inherits it immediately, no pod restart.');
      gh.refresh();
    } catch (failure) {
      setError(messageOf(failure));
    } finally {
      setBusy(false);
    }
  };

  const verify = async () => {
    setVerifying(true);
    setError(null);
    setResult(null);
    try {
      const v = await api.verifyGithub(token.trim().length === 0 ? undefined : token.trim());
      setResult(v);
    } catch (failure) {
      setError(messageOf(failure));
    } finally {
      setVerifying(false);
    }
  };

  const data = gh.reading.phase === 'ready' ? gh.reading.data : null;

  return (
    <Card>
      <CardHeader>
        <CardTitle as="h2">
          GitHub token{' '}
          <Badge variant="outline">
            {data === null ? '…' : `${data.source} • ${data.hasEffectiveToken ? (data.tokenPreview ?? 'set') : 'no token'}`}
          </Badge>
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div className="app-stack">
          <div className="app-field">
            <label className="app-label" htmlFor="gh-token">
              GH_TOKEN — paste to replace; blank verify uses the stored/secret token
            </label>
            <div className="app-row">
              <div className="app-enroll__input">
                <Input
                  id="gh-token"
                  type={show ? 'text' : 'password'}
                  value={token}
                  placeholder={
                    data?.hasStoredToken
                      ? 'stored — paste to replace; leave blank to keep'
                      : 'paste GH_TOKEN — saved in config store, masked here'
                  }
                  onChange={(event) => setToken(event.target.value)}
                  autoComplete="off"
                />
              </div>
              <Button type="button" size="sm" variant="outline" onClick={() => setShow((c) => !c)}>
                {show ? 'Hide' : 'Show'}
              </Button>
              <Button type="button" size="sm" onClick={() => void save()} disabled={busy}>
                Save
              </Button>
              <Button type="button" size="sm" variant="outline" onClick={() => void verify()} disabled={verifying}>
                {verifying ? 'Verifying…' : 'Verify'}
              </Button>
            </div>
            <p className="app-hint">
              Verify checks connectivity (@login), rate limit, scopes, and every enrolled repo for push access.
              Save is live immediately — the next poll uses it, no restart.
            </p>
          </div>
          {error === null ? null : <p className="app-error">{error}</p>}
          {note === null ? null : <p className="app-success">{note}</p>}
          {result === null ? null : (
            <Alert variant={result.ok ? undefined : 'destructive'}>
              <AlertTitle>{result.ok ? 'GitHub verify passed' : 'GitHub verify found problems'}</AlertTitle>
              <AlertDescription>
                <div>{result.message}</div>
                {result.rateLimitRemaining === null ? null : (
                  <div className="app-card-note">
                    rate {result.rateLimitRemaining}/{result.rateLimitLimit}
                    {result.scopes === null ? '' : ` • scopes: ${result.scopes}`}
                  </div>
                )}
                <div className="app-stack app-stack--sm">
                  {result.repos.map((r) => (
                    <div key={r.repo} className="app-card-note">
                      <Badge variant={r.ok ? 'success' : 'destructive'}>{r.ok ? 'ok' : 'fail'}</Badge>{' '}
                      <code className="app-code">{r.repo}</code> — {r.message}
                    </div>
                  ))}
                  {result.orgs.map((o) => (
                    <div key={o.org} className="app-card-note">
                      <Badge variant={o.ok ? 'success' : 'destructive'}>{o.ok ? 'ok' : 'fail'}</Badge>{' '}
                      <code className="app-code">org {o.org}</code> — {o.message}
                    </div>
                  ))}
                </div>
              </AlertDescription>
            </Alert>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

function EnrollForm({ reload }: { reload: () => void }) {
  const [kind, setKind] = useState<'repo' | 'org'>('repo');
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);

  const submit = async () => {
    setError(null);
    setNote(null);
    setBusy(true);
    try {
      const result = await api.enroll(kind, name);
      setNote(result.note ?? 'enrolled');
      setName('');
      reload();
    } catch (failure) {
      setError(messageOf(failure));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle as="h2">Enroll a target</CardTitle>
      </CardHeader>
      <CardContent>
        <form
          className="app-enroll"
          onSubmit={(event) => {
            event.preventDefault();
            void submit();
          }}
        >
          <div className="app-field app-enroll__input">
            <label className="app-label" htmlFor="enroll-name">
              Owner/name or org login
            </label>
            <Input
              id="enroll-name"
              value={name}
              placeholder="owner/name or org"
              onChange={(event) => setName(event.target.value)}
            />
          </div>
          <div className="app-field">
            <label className="app-label" htmlFor="enroll-kind">
              Kind
            </label>
            <NativeSelect
              id="enroll-kind"
              size="sm"
              value={kind}
              onChange={(event) => setKind(event.target.value === 'org' ? 'org' : 'repo')}
            >
              <option value="repo">repo</option>
              <option value="org">org</option>
            </NativeSelect>
          </div>
          <div className="app-field">
            <span className="app-label">&nbsp;</span>
            <Button type="submit" size="sm" disabled={busy}>
              {busy ? 'Probing gh…' : 'Enroll'}
            </Button>
          </div>
        </form>
        {error === null ? null : <p className="app-error">{error}</p>}
        {note === null ? null : <p className="app-success">{note}</p>}
        <p className="app-hint">
          Enrollment uses the authenticated <code className="app-code">gh</code> on this machine.
          A repo enrollment is owner/name; an org enrollment materializes every eligible repo the
          scan finds.
        </p>
      </CardContent>
    </Card>
  );
}

function SettingsDrawer({
  selection,
  pipelines,
  defaultName,
  onClose,
  onSaved,
  onUnenrolled,
}: {
  selection: { kind: 'target'; value: Target } | { kind: 'org'; value: Org } | null;
  pipelines: TargetsView['pipelines'];
  defaultName: string;
  onClose: () => void;
  onSaved: () => void;
  onUnenrolled: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirm, setConfirm] = useState(false);
  const [draft, setDraft] = useState<Target | Org | null>(null);

  useEffect(() => {
    setDraft(selection === null ? null : { ...selection.value });
    setConfirm(false);
    setError(null);
  }, [selection]);

  const save = async () => {
    if (selection === null || draft === null) return;
    setBusy(true);
    setError(null);
    try {
      if (selection.kind === 'target') {
        await api.updateTarget(draft as Target);
      } else {
        await api.updateOrg(draft as Org);
      }
      onSaved();
    } catch (failure) {
      setError(messageOf(failure));
    } finally {
      setBusy(false);
    }
  };

  const unenroll = async () => {
    if (selection === null) return;
    setBusy(true);
    setError(null);
    try {
      if (selection.kind === 'target') {
        await api.deleteTarget(selection.value.repo);
      } else {
        await api.deleteOrg(selection.value.org);
      }
      onUnenrolled();
    } catch (failure) {
      setError(messageOf(failure));
    } finally {
      setBusy(false);
    }
  };

  const title =
    selection === null
      ? 'Settings'
      : selection.kind === 'target'
        ? `Repo: ${selection.value.repo}`
        : `Org: ${selection.value.org}`;

  return (
    <Drawer
      open={selection !== null}
      onOpenChange={(open) => {
        if (!open) {
          setConfirm(false);
          setError(null);
          onClose();
        }
      }}
    >
      <DrawerContent side="right" closeLabel="Close settings">
        <DrawerHeader>
          <DrawerTitle>{title}</DrawerTitle>
        </DrawerHeader>
        <DrawerBody>
          {draft === null ? null : (
            <div className="app-drawer">
              <div className="app-field">
                <label className="app-label" htmlFor="drawer-intake">
                  Intake rule
                </label>
                <NativeSelect
                  id="drawer-intake"
                  size="sm"
                  value={draft.intake}
                  onChange={(event) => {
                    const value = event.target.value;
                    setDraft((current) =>
                      current === null ? current : { ...current, intake: value },
                    );
                  }}
                >
                  <option value="label">label — opt-in label (default)</option>
                  <option value="all">all — every open issue, oldest first</option>
                </NativeSelect>
              </div>

              <div className="app-field">
                <label className="app-label" htmlFor="drawer-pipeline">
                  Pipeline
                </label>
                <NativeSelect
                  id="drawer-pipeline"
                  size="sm"
                  value={draft.pipelineId ?? ''}
                  onChange={(event) => {
                    const value = event.target.value;
                    setDraft((current) =>
                      current === null
                        ? current
                        : { ...current, pipelineId: value.length > 0 ? value : null },
                    );
                  }}
                >
                  <option value="">(fleet default: {defaultName})</option>
                  {pipelines.map((pipeline) => (
                    <option key={pipeline.id} value={pipeline.id}>
                      {pipeline.name}
                    </option>
                  ))}
                </NativeSelect>
              </div>

              <div className="app-field">
                <label className="app-label" htmlFor="drawer-dod">
                  Definition of done (build/test commands)
                </label>
                <Input
                  id="drawer-dod"
                  className="app-mono"
                  value={draft.definitionOfDone}
                  onChange={(event) => {
                    const value = event.target.value;
                    setDraft((current) =>
                      current === null ? current : { ...current, definitionOfDone: value },
                    );
                  }}
                />
                <p className="app-hint">Free text — rendered into prompts via {'{{definition_of_done}}'}.</p>
              </div>

              <div className="app-row">
                <Switch
                  checked={draft.enabled}
                  aria-label="Enabled"
                  onCheckedChange={(checked) => {
                    setDraft((current) =>
                      current === null ? current : { ...current, enabled: checked },
                    );
                  }}
                />
                <span className="app-small">
                  Enabled — a disabled org stops scanning; a disabled repo stops intake and dequeue.
                </span>
              </div>

              {error === null ? null : <p className="app-error">{error}</p>}
            </div>
          )}
        </DrawerBody>
        <DrawerFooter>
          <div className="app-drawer__actions">
            <Button type="button" size="sm" onClick={() => void save()} disabled={busy}>
              Save settings
            </Button>
            {confirm ? (
              <>
                <Button
                  type="button"
                  size="sm"
                  variant="destructive"
                  onClick={() => void unenroll()}
                  disabled={busy}
                >
                  Really un-enroll?
                </Button>
                <Button
                  type="button"
                  size="sm"
                  variant="ghost"
                  onClick={() => setConfirm(false)}
                >
                  Keep it
                </Button>
              </>
            ) : (
              <Button
                type="button"
                size="sm"
                variant="outline"
                onClick={() => setConfirm(true)}
                disabled={busy}
              >
                Un-enroll…
              </Button>
            )}
          </div>
          <p className="app-hint">
            Un-enrolling stops intake, cancels queued runs and lets in-flight work finish. History
            stays on the board.
          </p>
        </DrawerFooter>
      </DrawerContent>
    </Drawer>
  );
}

function scanSummary(scan: OrgScan | undefined, targets: readonly Target[]): string {
  if (scan === undefined || scan.listedAt === null) {
    return 'first scan running…';
  }
  const skipped: string[] = [];
  const count = (predicate: (repo: OrgScan['repos'][number]) => boolean): number =>
    scan.repos.filter(predicate).length;
  const archived = count((repo) => repo.isArchived);
  const forks = count((repo) => repo.isFork);
  const issuesDisabled = count((repo) => !repo.hasIssuesEnabled);
  const github = count((repo) => repo.nameWithOwner.endsWith('/.github'));
  const individually = count((repo) =>
    targets.some((target) => target.repo === repo.nameWithOwner && target.org === null),
  );
  if (archived > 0) skipped.push(`${archived} archived`);
  if (forks > 0) skipped.push(`${forks} fork`);
  if (issuesDisabled > 0) skipped.push(`${issuesDisabled} issues-disabled`);
  if (github > 0) skipped.push(`${github} .github`);
  if (individually > 0) skipped.push(`${individually} individually-enrolled`);

  const eligible = scan.repos.length - archived - forks - issuesDisabled - github;
  return `${eligible} repos found${skipped.length === 0 ? '' : ` · skipped: ${skipped.join(', ')}`}`;
}
