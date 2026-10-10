'use client';

import { useCallback, useEffect, useState } from 'react';

import { Alert, AlertDescription, AlertTitle } from '@nanisoft/prism-ui/components/alert';
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from '@nanisoft/prism-ui/components/alert-dialog';
import { Button } from '@nanisoft/prism-ui/components/button';
import { Card, CardContent, CardHeader, CardTitle } from '@nanisoft/prism-ui/components/card';
import { Status } from '@nanisoft/prism-ui/components/status';
import { DataTable01, type DataTable01Labels } from '@nanisoft/prism-ui/blocks/data-table-01';
import { RecordForm01, type RecordForm01Issue } from '@nanisoft/prism-ui/blocks/record-form-01';
import type { ColumnSpec, FieldSpecGroup } from '@nanisoft/prism-ui/spec';

import {
  fetchProjects,
  removeProject,
  writeProject,
  type ProjectView,
  type ProjectWrite,
  type ProjectsView,
} from '@/lib/projects';

/**
 * What the page has read so far. Three states rather than a nullable list and a
 * boolean: a fetch that has not answered, one that answered, and one that failed are
 * three different things a reader is owed.
 */
type Reading =
  | { phase: 'loading' }
  | { phase: 'ready'; projects: ProjectsView }
  | { phase: 'error'; message: string };

/**
 * One row of the served list, as the record index draws it. The load state is the
 * factory's own judgement that the loader served this file, drawn as a tone and the
 * words beside it.
 */
type ProjectRow = Omit<ProjectView, 'state'> & {
  state: { tone: 'success'; label: string };
};

/** The columns of the served list, in the order a reader should meet them. */
const COLUMNS: readonly ColumnSpec[] = [
  { key: 'name', header: 'Project', kind: 'Typography' },
  { key: 'repoUrl', header: 'Repository', kind: 'Typography' },
  { key: 'workerImage', header: 'Worker image', kind: 'Typography' },
  { key: 'llmModel', header: 'Model', kind: 'Typography' },
  { key: 'githubKeyName', header: 'GitHub credential', kind: 'Typography' },
  { key: 'llmKeyName', header: 'LLM credential', kind: 'Typography' },
  { key: 'state', header: 'Load state', kind: 'Status' },
];

/**
 * Every string the record index draws, because the Block ships none of its own. The
 * search box narrows the list the page already fetched; it never asks the factory
 * anything.
 */
const LABELS: DataTable01Labels = {
  search: 'Search projects',
  filters: 'Filters',
  reset: 'Clear filters',
  columns: 'Columns',
  viewColumns: 'View columns',
  selectAll: 'Select every project on this page',
  selectRow: (row) => `Select ${String(row.name)}`,
  rowActions: 'Actions',
  sort: (column, direction) => `Sort by ${column} (${direction})`,
  selectedCount: (count) => `${count} selected`,
  selectedAllMatching: (count) => `${count} selected`,
  clearedSelection: 'Selection cleared',
  dismissSelection: 'Clear the selection',
  previous: 'Previous',
  next: 'Next',
  page: (page) => `Page ${page}`,
  empty: 'No project matches that search.',
};

/**
 * A Project's six values, as the record form fills them. The field keys carry the
 * project's own dotted shape (`repo.url`, `keys.github`), which is what the file states
 * and what the write endpoint expects.
 *
 * The six sit in one group deliberately. `RecordForm01` derives each field's `id` from
 * its index within a group under one form-wide prefix, so two groups would mint two
 * fields with the same `id` and the second group's labels would point at the first
 * group's controls. One group keeps every label attached to its own field; the group's
 * description carries what a second heading would have said.
 */
function groupsFor(editing: ProjectView | null): readonly FieldSpecGroup[] {
  return [
    {
      id: 'project',
      label: 'The project file',
      description:
        'The six values a project file states. The two credentials are the names of environment variables, never their values; the value is set on the Credentials surface.',
      fields: [
        {
          key: 'name',
          label: 'Name',
          kind: 'Input',
          required: true,
          placeholder: 'nexus',
          defaultValue: editing?.name,
        },
        {
          key: 'repo.url',
          label: 'Repository URL',
          kind: 'Input',
          required: true,
          autoComplete: 'url',
          placeholder: 'https://github.com/owner/repo',
          defaultValue: editing?.repoUrl,
        },
        {
          key: 'worker.image',
          label: 'Worker image',
          kind: 'Input',
          required: true,
          placeholder: 'ghcr.io/nanisoft/agent-factory-worker:1',
          defaultValue: editing?.workerImage,
        },
        {
          key: 'llm.model',
          label: 'Model',
          kind: 'Input',
          required: true,
          placeholder: 'anthropic/claude-sonnet-4-5',
          defaultValue: editing?.llmModel,
        },
        {
          key: 'keys.github',
          label: 'GitHub credential name',
          kind: 'Input',
          required: true,
          placeholder: 'NEXUS_GITHUB_TOKEN',
          defaultValue: editing?.githubKeyName,
        },
        {
          key: 'keys.llm',
          label: 'LLM credential name',
          kind: 'Input',
          required: true,
          placeholder: 'NEXUS_ANTHROPIC_API_KEY',
          defaultValue: editing?.llmKeyName,
        },
      ],
    },
  ];
}

/**
 * The Projects surface: the repositories the factory serves, the project files it
 * refused, and the one form that writes a project file.
 *
 * A client component, because the app is a static export and the only thing that knows
 * what the factory serves is its own loopback API. It re-decides nothing: the served
 * set and the refusals are what the loader reported, and a write's refusal is the
 * factory's own words. The one judgement left to the page is a required-field check,
 * which is a rendering fact rather than a validation of the file.
 */
export default function ProjectsPage() {
  const [reading, setReading] = useState<Reading>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);
  const [editing, setEditing] = useState<ProjectView | null>(null);
  const [formGeneration, setFormGeneration] = useState(0);
  const [issues, setIssues] = useState<readonly RecordForm01Issue[]>([]);
  const [submitError, setSubmitError] = useState('');
  const [notice, setNotice] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [search, setSearch] = useState('');

  useEffect(() => {
    const controller = new AbortController();

    fetchProjects(controller.signal)
      .then((projects) => setReading({ phase: 'ready', projects }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setReading({
          phase: 'error',
          message: error instanceof Error ? error.message : 'the factory could not be reached',
        });
      });

    return () => controller.abort();
  }, [attempt]);

  const retry = useCallback(() => {
    setReading({ phase: 'loading' });
    setAttempt((count) => count + 1);
  }, []);

  const reread = useCallback(async () => {
    const projects = await fetchProjects();
    setReading({ phase: 'ready', projects });
  }, []);

  const beginEdit = useCallback((project: ProjectView) => {
    setEditing(project);
    setIssues([]);
    setSubmitError('');
    setNotice(null);
    setActionError(null);
    setFormGeneration((generation) => generation + 1);
  }, []);

  const onSubmit = useCallback(
    (form: HTMLFormElement) => {
      const data = new FormData(form);
      const text = (key: string) => String(data.get(key) ?? '').trim();
      const project: ProjectWrite = {
        name: text('name'),
        repoUrl: text('repo.url'),
        workerImage: text('worker.image'),
        llmModel: text('llm.model'),
        githubKeyName: text('keys.github'),
        llmKeyName: text('keys.llm'),
      };

      const found: RecordForm01Issue[] = [];
      if (!project.name) found.push({ field: 'name', message: 'A project needs a name.' });
      if (!project.repoUrl)
        found.push({ field: 'repo.url', message: 'A project needs a repository URL.' });
      if (!project.workerImage)
        found.push({ field: 'worker.image', message: 'A project needs a worker image.' });
      if (!project.llmModel) found.push({ field: 'llm.model', message: 'A project needs a model.' });
      if (!project.githubKeyName)
        found.push({ field: 'keys.github', message: 'Name the GitHub credential.' });
      if (!project.llmKeyName)
        found.push({ field: 'keys.llm', message: 'Name the LLM credential.' });

      setIssues(found);
      if (found.length > 0) {
        setSubmitError('Every value is required before the file is written.');
        return;
      }

      setSubmitError('');
      setActionError(null);
      writeProject(project)
        .then(async (result) => {
          if (!result.ok) {
            setSubmitError(result.error ?? 'the factory would not write the file');
            return;
          }

          setEditing(null);
          setNotice(
            `Wrote factories/${project.name}.yaml. Project files are read at start, so this project is served after a restart of the factory.`,
          );
          setFormGeneration((generation) => generation + 1);
          await reread();
        })
        .catch((error: unknown) => {
          setSubmitError(error instanceof Error ? error.message : 'the factory could not be reached');
        });
    },
    [reread],
  );

  const remove = useCallback(
    async (name: string) => {
      setActionError(null);
      try {
        const result = await removeProject(name);
        if (!result.ok) {
          setActionError(result.error ?? 'the factory would not remove the file');
          return;
        }

        setEditing((current) => (current?.name === name ? null : current));
        setNotice(
          `Removed factories/${name}.yaml. Project files are read at start, so this change is served after a restart of the factory.`,
        );
        await reread();
      } catch (error: unknown) {
        setActionError(error instanceof Error ? error.message : 'the factory could not be reached');
      }
    },
    [reread],
  );

  const rows: ProjectRow[] =
    reading.phase === 'ready'
      ? reading.projects.projects
          .filter((project) =>
            [
              project.name,
              project.repoUrl,
              project.workerImage,
              project.llmModel,
              project.githubKeyName,
              project.llmKeyName,
            ].some((value) => value.toLowerCase().includes(search.toLowerCase())),
          )
          .map((project) => ({
            ...project,
            state: { tone: 'success' as const, label: 'Served' },
          }))
      : [];

  return (
    <main className="page">
      <header className="page__header">
        <h1 className="page__title">Projects</h1>
        <p className="page__lede">
          The repositories the factory serves, and the project files it refused. A project is one file in{' '}
          <code>factories/</code>: the file is the single source of truth, and the loader is the only thing
          that decides whether it is a project.
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
        <>
          {notice ? (
            <Alert>
              <AlertTitle>Project file written</AlertTitle>
              <AlertDescription>{notice}</AlertDescription>
            </Alert>
          ) : null}

          {actionError ? (
            <Alert variant="destructive">
              <AlertTitle>The factory refused</AlertTitle>
              <AlertDescription>{actionError}</AlertDescription>
            </Alert>
          ) : null}

          {reading.projects.rejections.length > 0 ? (
            <section className="projects__section">
              <Card>
                <CardHeader>
                  <CardTitle>Refused files</CardTitle>
                </CardHeader>
                <CardContent>
                  <ul className="projects__refusals">
                    {reading.projects.rejections.map((rejection) => (
                      <li key={rejection.fileName} className="projects__refusal">
                        <span className="projects__refusal-file">{rejection.fileName}</span>
                        <Status tone="warning" label={rejection.reason} />
                        <span>{rejection.message}</span>
                      </li>
                    ))}
                  </ul>
                </CardContent>
              </Card>
            </section>
          ) : null}

          <section className="projects__section">
            <DataTable01
              title="Served projects"
              description="Each file the loader read. A project here is one the factory polls and builds for."
              columns={COLUMNS}
              rows={rows}
              getRowId={(row) => String(row.name)}
              searchValue={search}
              onSearchChange={setSearch}
              pageCount={1}
              labels={LABELS}
              renderRowActions={(row) => {
                const project = row as unknown as ProjectView;
                return (
                  <div className="projects__row-actions">
                    <Button type="button" variant="outline" size="sm" onClick={() => beginEdit(project)}>
                      Edit
                    </Button>
                    <AlertDialog>
                      <AlertDialogTrigger>Remove</AlertDialogTrigger>
                      <AlertDialogContent>
                        <AlertDialogHeader>
                          <AlertDialogTitle>Remove {project.name}?</AlertDialogTitle>
                          <AlertDialogDescription>
                            This deletes factories/{project.sourceFile}. The project stays served until the
                            factory is restarted, and the file cannot be recovered from here.
                          </AlertDialogDescription>
                        </AlertDialogHeader>
                        <AlertDialogFooter>
                          <AlertDialogCancel>Cancel</AlertDialogCancel>
                          <AlertDialogAction onClick={() => void remove(project.name)}>
                            Remove
                          </AlertDialogAction>
                        </AlertDialogFooter>
                      </AlertDialogContent>
                    </AlertDialog>
                  </div>
                );
              }}
            />
          </section>

          <section className="projects__section">
            <Alert>
              <AlertTitle>A written project file is served after a restart</AlertTitle>
              <AlertDescription>
                A project added here is served: the factory will poll its issues and, with the credentials the
                project names, open and merge pull requests into it. Nothing is added by default, so a
                repository is only reached once you name it ,  scope each token to that one repository. Project
                files are read at start, so a change here is served after a restart of the factory.
              </AlertDescription>
            </Alert>

            <div className="projects__form">
              <RecordForm01
                key={formGeneration}
                title={editing ? `Edit ${editing.name}` : 'Add a project'}
                description="Fills exactly the six values a project file states."
                columns={2}
                groups={groupsFor(editing)}
                issues={issues}
                submitError={submitError || undefined}
                submitLabel="Write the project file"
                footerStart={
                  editing ? (
                    <Button
                      type="button"
                      variant="ghost"
                      onClick={() => {
                        setEditing(null);
                        setIssues([]);
                        setSubmitError('');
                        setFormGeneration((generation) => generation + 1);
                      }}
                    >
                      Add a new project instead
                    </Button>
                  ) : undefined
                }
                onSubmit={onSubmit}
              />
            </div>
          </section>
        </>
      )}
    </main>
  );
}
