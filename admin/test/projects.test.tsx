import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import ProjectsPage from '@/app/projects/page';
import projectsFixture from './fixtures/projects.json';

/**
 * The Projects surface, rendered the way the browser receives it, against a fixture
 * that mirrors the real `GET /api/projects` response.
 *
 * The tests are about what a reader can read and press: the served list with its load
 * state, the loader's own refusals, the six-value form, the restart statement, and the
 * two writes the page makes. The fetch is the only substituted thing; every Prism item
 * is the real one from the pinned package.
 */
function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  });
}

/**
 * A fetch that answers the page's three calls by method. The list is the fixture; a
 * write is accepted unless a test overrides it.
 */
function answerWith(overrides: { write?: () => Promise<unknown>; remove?: () => Promise<unknown> } = {}) {
  const fetchMock = vi.fn((_input: string, init?: RequestInit) => {
    const method = init?.method ?? 'GET';
    if (method === 'POST') return overrides.write?.() ?? jsonResponse({ ok: true, error: null });
    if (method === 'DELETE') return overrides.remove?.() ?? jsonResponse({ ok: true, error: null });
    return jsonResponse(projectsFixture);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the projects page', () => {
  it('renders the served list, its load state, the refusals and the form', async () => {
    const fetchMock = answerWith();

    render(<ProjectsPage />);

    // The served project, its six values and its load state, off the fixture.
    expect(await screen.findByText('nexus')).toBeTruthy();
    expect(screen.getByText('https://github.com/NaniSoft/nexus')).toBeTruthy();
    expect(screen.getByText('ghcr.io/nanisoft/agent-factory-worker:1')).toBeTruthy();
    expect(screen.getByText('anthropic/claude-sonnet-4-5')).toBeTruthy();
    expect(screen.getByText('NEXUS_GITHUB_TOKEN')).toBeTruthy();
    expect(screen.getByText('NEXUS_ANTHROPIC_API_KEY')).toBeTruthy();
    expect(screen.getByText('Served')).toBeTruthy();

    // The loader's own refusal, with its reason and message: the UI adds no rule.
    expect(screen.getByText('broken.yaml')).toBeTruthy();
    expect(screen.getByText('Partial')).toBeTruthy();
    expect(screen.getByText('repo.url is not an absolute http or https URL')).toBeTruthy();

    // The restart statement, on the surface where a repository is chosen.
    expect(screen.getAllByText(/served after a restart/i).length).toBeGreaterThan(0);

    // The form fills exactly the six values. The label carries a required mark, so the
    // matcher anchors on the name rather than matching the trailing glyph.
    expect(screen.getByLabelText(/^Name/)).toBeTruthy();
    expect(screen.getByLabelText(/^Repository URL/)).toBeTruthy();
    expect(screen.getByLabelText(/^Worker image/)).toBeTruthy();
    expect(screen.getByLabelText(/^Model/)).toBeTruthy();
    expect(screen.getByLabelText(/^GitHub credential name/)).toBeTruthy();
    expect(screen.getByLabelText(/^LLM credential name/)).toBeTruthy();

    // The one read, to the factory's own API off this app's base path.
    expect(fetchMock).toHaveBeenCalledWith('/api/projects', {
      cache: 'no-store',
      signal: expect.anything(),
    });
  });

  it('writes the six values the reader filled and says the file is served after a restart', async () => {
    const fetchMock = answerWith();

    render(<ProjectsPage />);

    await screen.findByText('nexus');

    fireEvent.change(screen.getByLabelText(/^Name/), { target: { value: 'atlas' } });
    fireEvent.change(screen.getByLabelText(/^Repository URL/), {
      target: { value: 'https://github.com/NaniSoft/atlas' },
    });
    fireEvent.change(screen.getByLabelText(/^Worker image/), {
      target: { value: 'ghcr.io/nanisoft/agent-factory-worker:1' },
    });
    fireEvent.change(screen.getByLabelText(/^Model/), {
      target: { value: 'anthropic/claude-sonnet-4-5' },
    });
    fireEvent.change(screen.getByLabelText(/^GitHub credential name/), {
      target: { value: 'ATLAS_GITHUB_TOKEN' },
    });
    fireEvent.change(screen.getByLabelText(/^LLM credential name/), {
      target: { value: 'ATLAS_ANTHROPIC_API_KEY' },
    });

    const save = screen.getByRole('button', { name: 'Write the project file' });
    fireEvent.submit(save.closest('form') as HTMLFormElement);

    // The factory's own acknowledgement, and the honest thing about when it takes effect.
    expect(await screen.findByText(/Wrote factories\/atlas\.yaml/)).toBeTruthy();

    const post = fetchMock.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === 'POST');
    const postInit = post?.[1] as RequestInit | undefined;
    expect(postInit).toBeTruthy();
    expect(JSON.parse(String(postInit?.body))).toEqual({
      name: 'atlas',
      repoUrl: 'https://github.com/NaniSoft/atlas',
      workerImage: 'ghcr.io/nanisoft/agent-factory-worker:1',
      llmModel: 'anthropic/claude-sonnet-4-5',
      githubKeyName: 'ATLAS_GITHUB_TOKEN',
      llmKeyName: 'ATLAS_ANTHROPIC_API_KEY',
    });
  });

  it('removes a project only behind a confirmation', async () => {
    const fetchMock = answerWith();

    render(<ProjectsPage />);

    await screen.findByText('nexus');

    // The trigger opens the confirmation; the file is not touched until it is answered.
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText('Remove nexus?')).toBeTruthy();
    expect(fetchMock.mock.calls.some(([, init]) => (init as RequestInit | undefined)?.method === 'DELETE')).toBe(
      false,
    );

    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove' }));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith('/api/projects/nexus', { method: 'DELETE' });
    });
    expect(await screen.findByText(/Removed factories\/nexus\.yaml/)).toBeTruthy();
  });
});
