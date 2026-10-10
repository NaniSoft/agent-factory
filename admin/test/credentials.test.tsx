import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import CredentialsPage from '@/app/credentials/page';
import credentialsFixture from './fixtures/credentials.json';

/**
 * The Credentials surface, rendered the way the browser receives it, against a fixture
 * that mirrors the real `GET /api/credentials` response. The tests are about what a
 * reader can read and what the page does with the factory's answer — and, above all,
 * what it never does with a value.
 */
const secret = 'sk-a-value-that-must-never-be-rendered';

function factoryAnswering(list: unknown = credentialsFixture) {
  const calls: { method: string; url: string; body: string | null }[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : String(input);
    const method = init?.method ?? 'GET';
    calls.push({ method, url, body: typeof init?.body === 'string' ? init.body : null });

    if (method === 'PUT') {
      return { ok: true, status: 200, json: async () => ({ name: 'written', set: true }) };
    }
    if (method === 'DELETE') {
      return { ok: true, status: 204, json: async () => ({}) };
    }
    return { ok: true, status: 200, json: async () => list };
  });
  vi.stubGlobal('fetch', fetchMock);
  return { fetchMock, calls };
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the credentials page', () => {
  it('lists each credential name with set or not set and renders no value', async () => {
    factoryAnswering();

    render(<CredentialsPage />);

    expect(await screen.findByText('NEXUS_ANTHROPIC_API_KEY')).toBeTruthy();
    expect(screen.getByText('NEXUS_GITHUB_TOKEN')).toBeTruthy();
    expect(screen.getByText('set')).toBeTruthy();
    expect(screen.getByText('not set')).toBeTruthy();
    expect(screen.queryByText(secret)).toBeNull();
  });

  it('posts the typed value once and clears it, never rendering it back', async () => {
    const { calls } = factoryAnswering();

    render(<CredentialsPage />);
    await screen.findByText('NEXUS_ANTHROPIC_API_KEY');

    fireEvent.change(screen.getByLabelText('Name'), {
      target: { value: 'NEXUS_ANTHROPIC_API_KEY' },
    });
    fireEvent.change(screen.getByLabelText('Value'), { target: { value: secret } });
    fireEvent.click(screen.getByRole('button', { name: 'Set credential' }));

    await waitFor(() => {
      expect(calls.some((call) => call.method === 'PUT')).toBe(true);
    });
    const put = calls.find((call) => call.method === 'PUT');
    expect(put?.url).toBe('/api/credentials/NEXUS_ANTHROPIC_API_KEY');
    expect(put?.body).toContain(secret);

    // The value is cleared the moment it is written and is never rendered back.
    await waitFor(() => {
      expect(screen.queryByDisplayValue(secret)).toBeNull();
    });
    expect(screen.queryByText(secret)).toBeNull();
  });

  it('removes a credential by name', async () => {
    const { calls } = factoryAnswering();

    render(<CredentialsPage />);
    await screen.findByText('NEXUS_ANTHROPIC_API_KEY');

    fireEvent.click(screen.getAllByRole('button', { name: 'Remove' })[0]!);

    await waitFor(() => {
      expect(calls.some((call) => call.method === 'DELETE')).toBe(true);
    });
    const deleted = calls.find((call) => call.method === 'DELETE');
    expect(deleted?.url).toBe('/api/credentials/NEXUS_ANTHROPIC_API_KEY');
  });
});
