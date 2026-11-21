'use client';

import { useCallback, useEffect, useRef, useState } from 'react';

import { getJson } from './api';
import { messageOf } from './format';

/**
 * What a page has read so far. Three states rather than a nullable value and a
 * boolean: a fetch that has not answered, one that answered, and one that
 * failed are three different things a reader is owed.
 */
export type Reading<T> =
  | { phase: 'loading' }
  | { phase: 'ready'; data: T }
  | { phase: 'error'; message: string };

/**
 * Read one API path, optionally on a cadence.
 *
 * `refresh` re-reads without blanking a page that is already showing data: a
 * save should update the surface it acted on, not flash it back to a skeleton.
 * Polling pauses while the tab is hidden and reads once when it returns.
 */
export function useApi<T>(path: string, pollMs?: number) {
  const [reading, setReading] = useState<Reading<T>>({ phase: 'loading' });
  const pathRef = useRef(path);
  pathRef.current = path;

  const read = useCallback(async (signal?: AbortSignal) => {
    try {
      const data = await getJson<T>(pathRef.current, signal);
      setReading({ phase: 'ready', data });
    } catch (error) {
      if (signal?.aborted === true) {
        return;
      }
      setReading({ phase: 'error', message: messageOf(error) });
    }
  }, []);

  useEffect(() => {
    setReading({ phase: 'loading' });
    const controller = new AbortController();
    void read(controller.signal);

    if (pollMs === undefined) {
      return () => controller.abort();
    }

    const interval = window.setInterval(() => {
      if (!document.hidden) {
        void read(controller.signal);
      }
    }, pollMs);
    const visibility = () => {
      if (!document.hidden) {
        void read(controller.signal);
      }
    };
    document.addEventListener('visibilitychange', visibility);

    return () => {
      controller.abort();
      window.clearInterval(interval);
      document.removeEventListener('visibilitychange', visibility);
    };
  }, [path, pollMs, read]);

  const refresh = useCallback(() => {
    void read();
  }, [read]);

  return { reading, refresh };
}
