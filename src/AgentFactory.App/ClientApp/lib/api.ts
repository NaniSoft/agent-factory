import type {
  Catalog,
  CommandResult,
  EnrollResult,
  Fleet,
  GithubToken,
  GithubVerifyResult,
  Org,
  PauseResult,
  Pipeline,
  PipelineCatalog,
  ProbeResult,
  ProfileSaveResult,
  ProfileVerifyResult,
  ProfilesView,
  RunDetail,
  RunSummary,
  SavePipelineResult,
  Target,
  TargetsView,
  TranscriptView,
} from './types';

/**
 * The JSON surface, in one place.
 *
 * The app is served by the same .NET process as `/api`, so the base is empty
 * and every call is same-origin. `next dev` proxies `/api` to the factory's dev
 * port, which is why development needs no second base either.
 */
const BASE = process.env.NEXT_PUBLIC_AGENT_FACTORY_API ?? '';

export class ApiError extends Error {
  readonly status: number;

  constructor(message: string, status: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  if (init?.body !== undefined) {
    headers.set('content-type', 'application/json');
  }

  const response = await fetch(`${BASE}${path}`, {
    cache: 'no-store',
    ...init,
    headers,
  });

  if (!response.ok) {
    const text = await response.text();
    let message = `the factory answered ${response.status}`;
    try {
      const body = JSON.parse(text) as {
        error?: unknown;
        errors?: unknown;
        refusal?: unknown;
        message?: unknown;
      };
      const first = Array.isArray(body.errors) ? body.errors[0] : undefined;
      const candidate = body.error ?? first ?? body.refusal ?? body.message;
      if (typeof candidate === 'string' && candidate.length > 0) {
        message = candidate;
      }
    } catch {
      // Not JSON: keep the status sentence.
    }
    throw new ApiError(message, response.status);
  }

  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

export async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  return request<T>(path, { signal });
}

export const api = {
  // Fleet
  fleet: () => request<Fleet>('/api/fleet'),
  setPaused: (paused: boolean) =>
    request<PauseResult>('/api/fleet/pause', {
      method: 'POST',
      body: JSON.stringify({ paused }),
    }),
  githubToken: () => request<GithubToken>('/api/fleet/github'),
  saveGithubToken: (token: string | null) =>
    request<GithubToken>('/api/fleet/github', {
      method: 'POST',
      body: JSON.stringify({ token }),
    }),
  verifyGithub: (token?: string) =>
    request<GithubVerifyResult>('/api/fleet/github/verify', {
      method: 'POST',
      body: JSON.stringify({ token: token ?? null }),
    }),
  verifyProfile: (profile: {
    id: string | null;
    flavour: string;
    apiKey?: string;
    modelId?: string;
    models: string[];
  }) =>
    request<ProfileVerifyResult>('/api/profiles/verify', {
      method: 'POST',
      body: JSON.stringify({
        id: profile.id,
        flavour: profile.flavour,
        apiKey: profile.apiKey ?? null,
        modelId: profile.modelId ?? null,
        models: profile.models,
      }),
    }),

  // Runs
  runs: () => request<RunSummary[]>('/api/runs'),
  run: (runId: string, phase?: number) =>
    request<RunDetail>(
      `/api/runs/${encodeURIComponent(runId)}${phase === undefined ? '' : `?phase=${phase}`}`,
    ),
  transcript: (runId: string, phase?: number) =>
    request<TranscriptView>(
      `/api/runs/${encodeURIComponent(runId)}/transcript${phase === undefined ? '' : `?phase=${phase}`}`,
    ),

  // Pipelines
  pipelines: () => request<PipelineCatalog>('/api/pipelines'),
  savePipeline: (pipeline: Pipeline) =>
    request<SavePipelineResult>('/api/pipelines', {
      method: 'POST',
      body: JSON.stringify(pipeline),
    }),
  setDefaultPipeline: (id: string) =>
    request<CommandResult>(`/api/pipelines/${encodeURIComponent(id)}/default`, {
      method: 'POST',
    }),
  deletePipeline: (id: string) =>
    request<CommandResult>(`/api/pipelines/${encodeURIComponent(id)}`, {
      method: 'DELETE',
    }),

  // Profiles
  profiles: () => request<ProfilesView>('/api/profiles'),
  catalog: () => request<Catalog>('/api/engine/catalog'),
  externalCatalog: () => request<Catalog>('/api/engine/catalog/external'),
  saveProfile: (profile: {
    id: string | null;
    name: string;
    flavour: string;
    apiKey?: string;
    clearKey?: boolean;
    models: string[];
  }) =>
    request<ProfileSaveResult>('/api/profiles', {
      method: 'POST',
      body: JSON.stringify({
        id: profile.id,
        name: profile.name,
        flavour: profile.flavour,
        apiKey: profile.apiKey ?? null,
        clearKey: profile.clearKey ?? false,
        models: profile.models,
      }),
    }),
  deleteProfile: (id: string) =>
    request<CommandResult>(`/api/profiles/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  setUtility: (profileId: string | null, modelId: string | null) =>
    request<CommandResult>('/api/profiles/utility', {
      method: 'PUT',
      body: JSON.stringify({ profileId, modelId }),
    }),
  setFallback: (profileId: string | null, modelId: string | null) =>
    request<CommandResult>('/api/profiles/fallback', {
      method: 'PUT',
      body: JSON.stringify({ profileId, modelId }),
    }),
  probeProfile: (profile: {
    id: string | null;
    flavour: string;
    apiKey?: string;
    models: string[];
  }) =>
    request<ProbeResult>('/api/profiles/probe', {
      method: 'POST',
      body: JSON.stringify({
        id: profile.id,
        flavour: profile.flavour,
        apiKey: profile.apiKey ?? null,
        models: profile.models,
      }),
    }),
  restartEngine: () => request<CommandResult>('/api/engine/restart', { method: 'POST' }),

  // Targets and orgs
  targets: () => request<TargetsView>('/api/targets'),
  enroll: (kind: 'repo' | 'org', name: string) =>
    request<EnrollResult>('/api/targets/enroll', {
      method: 'POST',
      body: JSON.stringify({ kind, name }),
    }),
  updateTarget: (target: Target) =>
    request<CommandResult>('/api/targets', {
      method: 'PUT',
      body: JSON.stringify(target),
    }),
  deleteTarget: (repo: string) =>
    request<CommandResult>(`/api/targets?repo=${encodeURIComponent(repo)}`, {
      method: 'DELETE',
    }),
  updateOrg: (org: Org) =>
    request<CommandResult>('/api/orgs', {
      method: 'PUT',
      body: JSON.stringify(org),
    }),
  deleteOrg: (org: string) =>
    request<CommandResult>(`/api/orgs?org=${encodeURIComponent(org)}`, {
      method: 'DELETE',
    }),
};
