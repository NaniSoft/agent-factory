import { fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import WorkItemPage from '@/app/work-items/page';
import detailFixture from './fixtures/work-item.json';

/**
 * The work-item detail, rendered the way the browser receives it, against a fixture that
 * mirrors the real `GET /api/work-items/{id}` response.
 *
 * The tests are about what a reader can read and what the page does with the factory's
 * answer: the header, every round with its change, the four diff states told apart, the
 * "left off" message of a bounded diff, the payload and the log, and the decisions reused
 * from the shared form. The fetch and the router are the only substituted things; every
 * Prism item is the real one from the pinned package.
 *
 * `useSearchParams` is mocked because a bare render has no Next app-router context to read
 * the id from; the mock reads a value each test sets, which is the same value the browser
 * would hand the real hook.
 */
const navigation = vi.hoisted(() => ({ search: new URLSearchParams() }));

vi.mock('next/navigation', () => ({
  useSearchParams: () => navigation.search,
}));

const ITEM = '11111111-1111-1111-1111-111111111111';

function answerWith(detail: unknown, status = 200) {
  const fetchMock = vi.fn(async () => ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => detail,
  }));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

beforeEach(() => {
  navigation.search = new URLSearchParams(`item=${ITEM}`);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the work-item detail page', () => {
  it('renders the work item header, its rounds, and links back to the Board', async () => {
    const fetchMock = answerWith(detailFixture);

    render(<WorkItemPage />);

    // The header, from the factory's own record: the issue, the project, the lane and the
    // round count, and the route judgement in its own words.
    expect(await screen.findByText('#42 Add the board endpoint')).toBeTruthy();
    const root = document.querySelector('[data-work-item]');
    expect(root).not.toBeNull();
    expect(root?.getAttribute('data-work-item')).toBe(ITEM);
    expect(screen.getByText('nexus')).toBeTruthy();
    expect(screen.getByText('round 3 of 3')).toBeTruthy();
    expect(screen.getByText('Review')).toBeTruthy();
    expect(document.querySelector('[data-route="Unrouted"]')?.textContent).toContain(
      'Nobody has looked at this issue yet',
    );

    // The one read, to the factory's own API off this app's base path.
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/work-items/${ITEM}`,
      { cache: 'no-store', signal: expect.anything() },
    );

    // Every round is on the page, and the back link is the Board's own route.
    expect(document.querySelectorAll('[data-round]').length).toBe(3);
    const back = screen.getByRole('link', { name: 'Back to the Board' });
    expect(back.getAttribute('href')).toBe('/');
  });

  it('renders each file of a diff as a collapsible section with its counts, the first open', async () => {
    answerWith(detailFixture);

    render(<WorkItemPage />);
    await screen.findByText('#42 Add the board endpoint');

    const files = document.querySelectorAll('[data-round="1"] [data-diff-file]');
    expect(files.length).toBe(2);

    // The path, what git says happened and the line counts are on the summary line, so the
    // shape of the change costs one line per file.
    const first = files[0] as HTMLElement;
    expect(first.getAttribute('data-diff-file')).toBe('src/Index.cs');
    expect(first.getAttribute('data-change')).toBe('added');
    expect(first.getAttribute('data-added')).toBe('2');
    expect(first.getAttribute('data-removed')).toBe('0');
    expect(within(first).getByText('src/Index.cs')).toBeTruthy();
    expect(within(first).getByText('+2 −0')).toBeTruthy();

    // The first section is open and the rest are closed, and every file's own git text is
    // on the page either way — folded is one click, not summarised away.
    expect(first.getAttribute('data-open')).toBe('true');
    expect((files[1] as HTMLElement).getAttribute('data-open')).toBe('false');
    expect(within(first).getByText(/diff --git a\/src\/Index.cs/)).toBeTruthy();
    expect(within(files[1] as HTMLElement).getByText(/diff --git a\/src\/Other.cs/)).toBeTruthy();
  });

  it('tells a round that did not finish apart from a round that changed nothing', async () => {
    answerWith(detailFixture);

    render(<WorkItemPage />);
    await screen.findByText('#42 Add the board endpoint');

    // Two rounds over the same unchanged disk, and the page says two different things: the
    // round that ran to completion changed nothing, and the round that did not finish says
    // so rather than reading as one that chose to leave it alone.
    const empty = document.querySelector('[data-round="2"] [data-diff-state="empty"]');
    const unfinished = document.querySelector('[data-round="3"] [data-diff-state="unfinished"]');
    expect(empty).not.toBeNull();
    expect(unfinished).not.toBeNull();

    expect(empty?.textContent).toContain('Empty');
    expect(empty?.textContent).toContain('the round ran to completion and changed nothing');
    expect(empty?.textContent).not.toContain('Unfinished');

    expect(unfinished?.textContent).toContain('Unfinished');
    expect(unfinished?.textContent).toContain('did not run to completion');
    expect(unfinished?.textContent).not.toContain('Empty. Nothing on disk differs');
  });

  it('says how much a bounded diff left off and where the tree is', async () => {
    answerWith(detailFixture);

    render(<WorkItemPage />);
    await screen.findByText('#42 Add the board endpoint');

    const omitted = document.querySelector('[data-round="1"] [data-diff-omitted]');
    expect(omitted).not.toBeNull();
    expect(omitted?.textContent).toContain('not on this page');
    expect(omitted?.textContent).toContain('/rounds/abc/round-1/attempt-1/tree');
  });

  it('renders a round\'s payload and log', async () => {
    answerWith(detailFixture);

    render(<WorkItemPage />);
    await screen.findByText('#42 Add the board endpoint');

    expect(document.querySelector('[data-round="1"] [data-result="1"]')?.textContent).toContain(
      'the unit tests',
    );
    expect(document.querySelector('[data-round="1"] [data-log="1"]')?.textContent).toContain(
      'round 1 log',
    );
    // The agent's own sentence, the one part of a result nothing above it is derived from.
    expect(document.querySelector('[data-note="1"]')?.textContent).toContain('Added the endpoint.');
  });

  it('renders the recorded decisions and offers the shared decision form', async () => {
    answerWith(detailFixture);

    render(<WorkItemPage />);
    await screen.findByText('#42 Add the board endpoint');

    // What the reviewer did, in their own words, and where the loop put the work item.
    const made = document.querySelector('[data-decision-made="request-changes"]');
    expect(made).not.toBeNull();
    expect(made?.getAttribute('data-applied-to')).toBe('Frontier');
    expect(made?.textContent).toContain('Move the null check inside the lock.');
    expect(made?.textContent).toContain('applied to Frontier');

    // The shared form, offering exactly the decisions the factory offered.
    expect(screen.getByRole('button', { name: 'Approve' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Request changes' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Reject' })).toBeTruthy();
  });

  it('posts a decision through the shared form', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST') {
        return {
          ok: true,
          status: 200,
          json: async () => ({ applied: true, refusal: null, resultingLane: 'Done' }),
        };
      }
      return { ok: true, status: 200, json: async () => detailFixture };
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<WorkItemPage />);
    await screen.findByText('#42 Add the board endpoint');

    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));

    // The one call the form makes, to the factory's own route for the work item.
    await screen.findByText('#42 Add the board endpoint');
    expect(fetchMock).toHaveBeenCalledWith(
      `/api/work-items/${ITEM}/decisions`,
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('says so when no work item was named', async () => {
    navigation.search = new URLSearchParams();
    answerWith(detailFixture);

    render(<WorkItemPage />);

    expect(await screen.findByText('No work item was named')).toBeTruthy();
  });

  it('says so, and offers a retry, when the factory does not answer', async () => {
    const fetchMock = answerWith({}, 500);

    render(<WorkItemPage />);

    expect(await screen.findByText('the factory answered 500')).toBeTruthy();
    const retry = screen.getByRole('button', { name: 'Try again' });

    fetchMock.mockImplementation(async () => ({
      ok: true,
      status: 200,
      json: async () => detailFixture,
    }));
    fireEvent.click(retry);

    expect(await screen.findByText('#42 Add the board endpoint')).toBeTruthy();
  });
});
