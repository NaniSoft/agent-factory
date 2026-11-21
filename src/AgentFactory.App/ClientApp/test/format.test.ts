import { describe, expect, it } from 'vitest';

import {
  compact,
  formatDuration,
  formatTokens,
  lastSegment,
  stateTone,
  stateVariant,
} from '@/lib/format';

describe('run state presentation', () => {
  it('maps each state to a tone and a badge variant', () => {
    expect(stateTone('succeeded')).toBe('success');
    expect(stateVariant('succeeded')).toBe('success');
    expect(stateTone('failed')).toBe('destructive');
    expect(stateVariant('failed')).toBe('destructive');
    expect(stateTone('disarmed')).toBe('warning');
    expect(stateVariant('queued')).toBe('secondary');
  });

  it('formats a duration as minutes and seconds', () => {
    expect(formatDuration('2026-01-01T00:00:00Z', '2026-01-01T00:02:07Z')).toBe('02:07');
    expect(formatDuration('2026-01-01T00:00:00Z', null)).toBe('—');
  });

  it('compacts large token counts', () => {
    expect(compact(999)).toBe('999');
    expect(compact(1200)).toBe('1.2k');
    expect(compact(2_400_000)).toBe('2.4m');
    expect(formatTokens(1200, 3400)).toBe('1.2k→3.4k');
  });

  it('keeps the last segment of a provider-scoped model id', () => {
    expect(lastSegment('opencode/grok-4.7')).toBe('grok-4.7');
    expect(lastSegment('sonnet')).toBe('sonnet');
    expect(lastSegment(null)).toBe('?');
  });
});
