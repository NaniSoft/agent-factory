import type { StatusTone } from '@nanisoft/prism-ui/components/status';

import type { RunKind, RunState } from './types';

export type BadgeVariant =
  | 'default'
  | 'secondary'
  | 'destructive'
  | 'success'
  | 'warning'
  | 'outline';

const pad = (value: number) => String(value).padStart(2, '0');

/** The dot tone a run state is drawn in. */
export function stateTone(state: RunState): StatusTone {
  switch (state) {
    case 'succeeded':
      return 'success';
    case 'failed':
      return 'destructive';
    case 'working':
      return 'info';
    case 'disarmed':
      return 'warning';
    case 'queued':
    case 'routed':
    case 'cancelled':
    default:
      return 'neutral';
  }
}

/** The badge a run state is drawn in, beside its own word. */
export function stateVariant(state: RunState): BadgeVariant {
  switch (state) {
    case 'succeeded':
      return 'success';
    case 'failed':
      return 'destructive';
    case 'working':
      return 'default';
    case 'disarmed':
      return 'warning';
    case 'queued':
      return 'secondary';
    case 'routed':
    case 'cancelled':
    default:
      return 'outline';
  }
}

export function stateLabel(state: RunState): string {
  switch (state) {
    case 'queued':
      return 'queued';
    case 'working':
      return 'working';
    case 'succeeded':
      return 'succeeded';
    case 'failed':
      return 'failed';
    case 'cancelled':
      return 'cancelled';
    case 'disarmed':
      return 'disarmed';
    case 'routed':
      return 'routed';
    default:
      return state;
  }
}

export function kindLabel(kind: RunKind): string {
  switch (kind) {
    case 'normal':
      return 'single repo';
    case 'parent':
      return 'cross-repo parent';
    case 'child':
      return 'child run';
    default:
      return kind;
  }
}

/** `MM-dd HH:mm:ss`, in the reader's local time. */
export function formatWhen(iso: string | null): string {
  if (iso === null) {
    return '—';
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return iso;
  }
  return (
    `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
  );
}

export function formatClock(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return iso;
  }
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

/** `mm:ss`, or `h:mm:ss` once a run has been going long enough to need it. */
export function formatDuration(start: string, end: string | null): string {
  if (end === null) {
    return '—';
  }
  const from = new Date(start).getTime();
  const to = new Date(end).getTime();
  if (Number.isNaN(from) || Number.isNaN(to) || to < from) {
    return '—';
  }
  const total = Math.floor((to - from) / 1000);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  return hours > 0
    ? `${hours}:${pad(minutes)}:${pad(seconds)}`
    : `${pad(minutes)}:${pad(seconds)}`;
}

export function formatCost(cost: number): string {
  return `$${cost.toFixed(4)}`;
}

export function formatTokens(tokensIn: number, tokensOut: number): string {
  return `${compact(tokensIn)}→${compact(tokensOut)}`;
}

export function compact(value: number): string {
  if (value >= 1_000_000) {
    return `${(value / 1_000_000).toFixed(1)}m`;
  }
  if (value >= 1_000) {
    return `${(value / 1_000).toFixed(1)}k`;
  }
  return String(value);
}

/** How long ago a moment was, in the shortest honest unit. */
export function formatAgo(iso: string | null): string {
  if (iso === null) {
    return 'not yet';
  }
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) {
    return iso;
  }
  const seconds = Math.max(0, Math.floor((Date.now() - then) / 1000));
  if (seconds < 60) {
    return `${seconds} s ago`;
  }
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes} min ago`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours} h ago`;
  }
  return `${Math.floor(hours / 24)} d ago`;
}

/** The model id after its last slash (`opencode/grok-4.7` → `grok-4.7`). */
export function lastSegment(modelId: string | null): string {
  if (modelId === null || modelId.length === 0) {
    return '?';
  }
  const slash = modelId.lastIndexOf('/');
  return slash >= 0 ? modelId.slice(slash + 1) : modelId;
}

export function messageOf(error: unknown): string {
  if (error instanceof Error && error.message.length > 0) {
    return error.message;
  }
  return 'the factory could not be reached';
}
