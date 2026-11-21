/**
 * The JSON the factory answers, as the app reads it.
 *
 * These mirror the records in `Api/Views.cs`. They are hand-written rather than
 * generated because the surface is small and the app is the only consumer; the
 * cost is that a rename on the server is a `tsc` error here rather than a
 * compile-time check, which is why the view file is the one to read beside a
 * page.
 */

export type RunState =
  | 'queued'
  | 'working'
  | 'succeeded'
  | 'failed'
  | 'cancelled'
  | 'disarmed'
  | 'routed';

export type RunKind = 'normal' | 'parent' | 'child';

export interface RunSummary {
  runId: string;
  repo: string;
  issueNumber: number;
  issueTitle: string;
  issueUrl: string;
  state: RunState;
  kind: RunKind;
  attempt: number;
  branch: string | null;
  prUrl: string | null;
  pipelineName: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  error: string | null;
  parentRunId: string | null;
  childOrder: number;
  live: boolean;
  triageVerdict: string | null;
  triageSummary: string | null;
  routedIssueUrl: string | null;
}

export interface Phase {
  name: string;
  skills: string[];
  sessionId: string | null;
  startedAt: string;
  finishedAt: string | null;
  cost: number;
  tokensIn: number;
  tokensOut: number;
  summary: string | null;
  profileName: string | null;
  flavour: string | null;
  modelId: string | null;
  providerId: string | null;
}

export interface Session {
  id: string;
  title: string;
  directory: string;
  agent: string;
  modelId: string;
  cost: number;
  tokensIn: number;
  tokensOut: number;
  updated: string;
}

export interface TranscriptLine {
  time: string;
  role: string;
  text: string;
}

export interface RunDetail {
  run: RunSummary;
  issueBody: string;
  agentSummary: string | null;
  phases: Phase[];
  logTail: string | null;
  children: RunSummary[];
  parent: RunSummary | null;
  session: Session | null;
  transcript: TranscriptLine[];
  selectedPhaseIndex: number;
  selectedSessionId: string | null;
}

export interface TranscriptView {
  selectedPhaseIndex: number;
  sessionId: string | null;
  session: Session | null;
  transcript: TranscriptLine[];
}

export interface RunCounts {
  total: number;
  succeeded: number;
  failed: number;
  working: number;
  queued: number;
  cancelled: number;
  disarmed: number;
  routed: number;
}

export interface Fleet {
  app: string;
  tagline: string;
  engine: { up: boolean; port: number };
  pollSeconds: number;
  targetCount: number;
  optInLabel: string;
  paused: boolean;
  runs: RunCounts;
  now: string;
}

export interface PhaseDefinition {
  name: string;
  skills: string[];
  template: string;
  profileId: string | null;
  modelId: string | null;
}

export interface Pipeline {
  id: string;
  name: string;
  phases: PhaseDefinition[];
}

export interface ProfileSummary {
  id: string;
  name: string;
  flavour: string;
  flavourLabel: string;
  models: string[];
  hasKey: boolean;
}

export interface PipelineCatalog {
  pipelines: Pipeline[];
  defaultPipelineId: string;
  profiles: ProfileSummary[];
  skills: string[];
  placeholders: string[];
}

export interface Flavour {
  value: string;
  label: string;
  executable: boolean;
  engine: string;
  fact: string;
  keyMode: string;
  catalogNote: string;
  keylessNote: string;
  staticCatalog: string[];
  externalCatalogUrl: string | null;
}

export interface UtilityBinding {
  profileId: string | null;
  modelId: string | null;
}

export interface ProfilesView {
  profiles: ProfileSummary[];
  utility: UtilityBinding | null;
  fallback: UtilityBinding | null;
  flavours: Flavour[];
  referrers: Record<string, string[]>;
}

export interface Provider {
  id: string;
  name: string;
  models: string[];
}

export interface Catalog {
  available: boolean;
  providers: Provider[];
}

export interface ProbeResult {
  ok: boolean;
  message: string;
  keyRejected: boolean;
}

export interface GithubToken {
  hasStoredToken: boolean;
  hasEffectiveToken: boolean;
  source: string;
  tokenPreview: string | null;
}

export interface GhRepoCheck {
  repo: string;
  ok: boolean;
  message: string;
  push: boolean | null;
  pull: boolean | null;
  defaultBranch: string | null;
}

export interface GhOrgCheck {
  org: string;
  ok: boolean;
  message: string;
  role: string | null;
}

export interface GithubVerifyResult {
  ok: boolean;
  message: string;
  userLogin: string | null;
  scopes: string | null;
  rateLimitRemaining: number | null;
  rateLimitLimit: number | null;
  repos: GhRepoCheck[];
  orgs: GhOrgCheck[];
}

export interface ProfileVerifyResult {
  ok: boolean;
  message: string;
  modelReply: string | null;
  modelId: string | null;
  providerId: string | null;
  tokensIn: number;
  tokensOut: number;
  cost: number;
  keyRejected: boolean;
}

export interface Target {
  repo: string;
  org: string | null;
  intake: string;
  pipelineId: string | null;
  enabled: boolean;
  definitionOfDone: string;
}

export interface Org {
  org: string;
  intake: string;
  pipelineId: string | null;
  enabled: boolean;
  definitionOfDone: string;
}

export interface TargetPoll {
  at: string;
  note: string | null;
}

export interface PipelineChoice {
  id: string;
  name: string;
}

export interface OrgRepo {
  nameWithOwner: string;
  isArchived: boolean;
  isFork: boolean;
  hasIssuesEnabled: boolean;
}

export interface OrgScan {
  listedAt: string | null;
  repos: OrgRepo[];
}

export interface TargetsView {
  targets: Target[];
  orgs: Org[];
  pipelines: PipelineChoice[];
  defaultPipelineId: string;
  lastPoll: Record<string, TargetPoll>;
  queued: Record<string, number>;
  orgScans: Record<string, OrgScan>;
}

export interface EnrollResult {
  ok: boolean;
  error: string | null;
  note: string | null;
}

export interface CommandResult {
  ok: boolean;
  error: string | null;
}

export interface SavePipelineResult {
  ok: boolean;
  errors: string[];
  id: string | null;
}

export interface ProfileSaveResult {
  ok: boolean;
  errors: string[];
  profile: ProfileSummary | null;
}

export interface PauseResult {
  paused: boolean;
}
