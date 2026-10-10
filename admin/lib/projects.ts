/**
 * The Projects surface's view model and the calls that read and write it.
 *
 * The shape mirrors `AgentFactory.Api.ProjectsView`: the served set with the load
 * state the loader decided for each project, and the files it refused with its own
 * reason and message. Camel-cased by name on both sides and pinned there rather than
 * left to a serialiser's naming policy. There is deliberately no codegen between the
 * two: the C# records are the contract on the factory side and a fixture that
 * mirrors the endpoint's own response is what keeps them in step.
 *
 * Nothing here validates: the factory's loader is the only judge of a project file,
 * and a refusal it writes is rendered as it wrote it.
 */
export type ProjectView = {
  name: string;
  repoUrl: string;
  workerImage: string;
  llmModel: string;
  githubKeyName: string;
  llmKeyName: string;
  sourceFile: string;
  state: string;
};

export type ProjectRejectionView = {
  fileName: string;
  reason: string;
  message: string;
};

export type ProjectsView = {
  projects: ProjectView[];
  rejections: ProjectRejectionView[];
};

/** The six values a project file states, exactly as the form fills them. */
export type ProjectWrite = {
  name: string;
  repoUrl: string;
  workerImage: string;
  llmModel: string;
  githubKeyName: string;
  llmKeyName: string;
};

/** A write's outcome: whether it took, and the factory's words when it did not. */
export type WriteResult = {
  ok: boolean;
  error: string | null;
};

/**
 * Read the factory's served projects and its refusals.
 *
 * The API is the factory's own process and lives off this app's base path, at
 * `/api/projects`, so the request is absolute from the origin rather than relative to
 * `/admin`. `cache: 'no-store'` because a project added since the last read is served
 * only after a restart, and a cached list would hide that.
 */
export async function fetchProjects(signal?: AbortSignal): Promise<ProjectsView> {
  const response = await fetch('/api/projects', { cache: 'no-store', signal });
  if (!response.ok) {
    throw new Error(`the factory answered ${response.status}`);
  }

  return (await response.json()) as ProjectsView;
}

/**
 * Write a project file, adding one or overwriting the one the name already has.
 *
 * A refusal is the factory's, carried in the body's `error`, and it is read whether
 * the response says 200 or 400: the renderer shows the factory's words and invents
 * none of its own.
 */
export async function writeProject(project: ProjectWrite): Promise<WriteResult> {
  const response = await fetch('/api/projects', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(project),
  });

  return (await response.json()) as WriteResult;
}

/**
 * Remove one project's file. The name is the address, and the factory refuses a name
 * it would never serve rather than deleting a path it was handed.
 */
export async function removeProject(name: string): Promise<WriteResult> {
  const response = await fetch(`/api/projects/${encodeURIComponent(name)}`, {
    method: 'DELETE',
  });

  return (await response.json()) as WriteResult;
}
