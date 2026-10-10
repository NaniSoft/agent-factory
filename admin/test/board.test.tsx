import { fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import BoardPage from '@/app/page';
import boardFixture from './fixtures/board.json';

/**
 * The Board, rendered the way the browser receives it, against a fixture that
 * mirrors the real `GET /api/board` response.
 *
 * The tests are about what a reader can read and what the page does with the
 * factory's answer: the lanes as columns, a card's project tag and round count,
 * the project filter narrowing the cards, the budget and the auto-merge mode, and
 * the three states a reader meets before and instead of an answer. The fetch and
 * the router are the only substituted things; every Prism item is the real one
 * from the pinned package.
 *
 * `useSearchParams` is mocked because a bare render has no Next app-router context
 * to read a query string from; the mock reads a value each test sets, which is the
 * same value the browser would hand the real hook.
 */
const navigation = vi.hoisted(() => ({ search: new URLSearchParams() }));

vi.mock('next/navigation', () => ({
  useSearchParams: () => navigation.search,
}));

function answerWith(board: unknown, status = 200) {
  const fetchMock = vi.fn(async () => ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => board,
  }));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

/** A board with the eight lanes and nothing in any of them. */
const emptyBoard = {
  budget: { inUse: 0, of: 2 },
  autoMerge: false,
  lanes: [
    { lane: 'Backlog', label: 'Backlog', cards: [] },
    { lane: 'Frontier', label: 'Frontier', cards: [] },
    { lane: 'InProgress', label: 'In Progress', cards: [] },
    { lane: 'Review', label: 'Review', cards: [] },
    { lane: 'Done', label: 'Done', cards: [] },
    { lane: 'Escalated', label: 'Escalated', cards: [] },
    { lane: 'Rejected', label: 'Rejected', cards: [] },
  ],
  projects: [],
  rejections: [],
  intake: {
    status: 'polled',
    summary: 'The factory is serving no projects, so there is nothing to poll.',
    projects: 0,
    polled: 0,
    neverPolled: 0,
    failing: 0,
    rows: [],
  },
};

beforeEach(() => {
  navigation.search = new URLSearchParams();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the board page', () => {
  it('renders the budget and the auto-merge mode the factory returned', async () => {
    const fetchMock = answerWith(boardFixture);

    render(<BoardPage />);

    // The budget in the factory's own words, and off the real numbers in the
    // fixture rather than a string the page composed on its own.
    expect(await screen.findByText('2 of 2 worker containers in use')).toBeTruthy();
    expect(screen.getByText('auto-merge is on')).toBeTruthy();

    // The one call, to the factory's own API off this app's base path.
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith('/api/board', { cache: 'no-store', signal: expect.anything() });
  });

  it('renders the lanes as columns and a card with its project tag and round count', async () => {
    answerWith(boardFixture);

    render(<BoardPage />);

    expect(await screen.findByText('Add the board endpoint')).toBeTruthy();

    // The lanes are present as columns, in the factory's own order and with the
    // lane's own words. Read off the Block's own markers rather than a heading that
    // could be reworded. The factory renders five swimlanes and then the two
    // endings, Escalated and Rejected: seven columns, and the park/final ones last.
    const lanes = ['Backlog', 'Frontier', 'InProgress', 'Review', 'Done', 'Escalated', 'Rejected'];
    for (const lane of lanes) {
      expect(document.querySelector(`[data-column="${lane}"]`)).toBeTruthy();
    }
    expect(document.querySelectorAll('[data-column]').length).toBe(7);

    // The count beside a column's name is the number of cards in it.
    const review = document.querySelector('[data-column="Review"]');
    expect(review?.querySelector('[data-slot="kanban-01-count"]')?.textContent).toBe('2');

    // A card carries its title, its Project as a tag, its lane's state and its
    // round count. The tag is read inside the card, because the project name also
    // appears as a filter choice above the lanes.
    const card = screen.getByText('Add the board endpoint').closest('[data-slot="kanban-01-card"]');
    expect(card).not.toBeNull();
    expect(within(card as HTMLElement).getByText('nexus')).toBeTruthy();
    expect(within(card as HTMLElement).getByText('round 1 of 3')).toBeTruthy();
    expect(within(card as HTMLElement).getByText('Review')).toBeTruthy();
  });

  it('narrows the cards to the project in force and says which one', async () => {
    navigation.search = new URLSearchParams('project=prism');
    answerWith(boardFixture);

    render(<BoardPage />);

    // Only the narrowed project's cards are drawn.
    expect(await screen.findByText('Tighten the focus ring')).toBeTruthy();
    expect(screen.queryByText('Add the board endpoint')).toBeNull();
    expect(screen.queryByText('Ship the merger')).toBeNull();

    // The board states the filter in force, and every served project stays on offer.
    const inForce = document.querySelector('[data-project-in-force]');
    expect(inForce?.getAttribute('data-project-in-force')).toBe('prism');
    expect(inForce?.textContent).toContain('prism');
    expect(screen.getByRole('button', { name: 'nexus' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'prism' })).toBeTruthy();
  });

  it('carries the loopback trust statement', async () => {
    answerWith(boardFixture);

    render(<BoardPage />);

    expect(await screen.findByText(/Only this machine can decide what merges/)).toBeTruthy();
  });

  it('renders a row per served project with its intake state, above the lanes', async () => {
    answerWith(boardFixture);

    render(<BoardPage />);
    await screen.findByText('Add the board endpoint');

    // The section, with the factory's own worst state and its own sentence about an empty
    // Backlog, read off the section's markers rather than a heading that could be reworded.
    const section = document.querySelector('[data-intake="intake"]');
    expect(section).not.toBeNull();
    expect(section?.getAttribute('data-intake-state')).toBe('failing');
    expect(
      within(section as HTMLElement).getByText(
        /An empty Backlog does not mean there is nothing to do while intake is broken/,
      ),
    ).toBeTruthy();

    // One row per served project, each carrying its own state: the three states land at
    // once, which is the whole point of the section.
    const nexus = document.querySelector('[data-intake-project="nexus"]');
    const prism = document.querySelector('[data-intake-project="prism"]');
    const zeta = document.querySelector('[data-intake-project="zeta"]');
    expect(nexus?.getAttribute('data-intake-state')).toBe('failing');
    expect(prism?.getAttribute('data-intake-state')).toBe('polled');
    expect(zeta?.getAttribute('data-intake-state')).toBe('never-polled');

    // A polled row says what it found, and the state reads as words rather than as a slug.
    expect(prism?.getAttribute('data-open-issues')).toBe('2');
    expect(within(prism as HTMLElement).getByText('polled')).toBeTruthy();

    // A failing row shows its classification and when it will be asked again — `never` for
    // a permanent failure, which is the one a reader has to be able to see.
    expect(nexus?.getAttribute('data-classification')).toBe('Permanent');
    expect(nexus?.getAttribute('data-again')).toBe('never');
    expect(within(nexus as HTMLElement).getByText('Permanent')).toBeTruthy();

    // It is above the lanes: the section precedes the first kanban column.
    const firstColumn = document.querySelector('[data-column]');
    expect(firstColumn).not.toBeNull();
    expect(
      section!.compareDocumentPosition(firstColumn!) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  it('does not narrow the intake section by the project filter', async () => {
    // The filter is a way of looking at work items, not at the machine: narrowing to prism
    // must not hide that nexus cannot be read, or the filter could make a broken factory
    // look like a working one.
    navigation.search = new URLSearchParams('project=prism');
    answerWith(boardFixture);

    render(<BoardPage />);
    await screen.findByText('Tighten the focus ring');

    // The filter does narrow the cards...
    expect(screen.queryByText('Add the board endpoint')).toBeNull();

    // ...and it leaves every served project's intake row on the board.
    expect(document.querySelector('[data-intake-project="nexus"]')).not.toBeNull();
    expect(document.querySelector('[data-intake-project="prism"]')).not.toBeNull();
    expect(document.querySelector('[data-intake-project="zeta"]')).not.toBeNull();
  });

  it('shows an empty state when nothing is on the board', async () => {
    answerWith(emptyBoard);

    render(<BoardPage />);

    expect(await screen.findByText('Nothing on the board yet')).toBeTruthy();
    expect(screen.queryByText(/worker containers in use/)).not.toBeNull();
  });

  it('shows the loading state before the factory has answered', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));

    render(<BoardPage />);

    expect(screen.getByText('Reading the factory')).toBeTruthy();
    expect(screen.queryByText(/worker containers in use/)).toBeNull();
  });

  it('says so, and offers a retry, when the factory does not answer', async () => {
    const fetchMock = answerWith({}, 500);

    render(<BoardPage />);

    expect(await screen.findByText('the factory answered 500')).toBeTruthy();
    const retry = screen.getByRole('button', { name: 'Try again' });

    // Pressing retry reads the factory again rather than showing the same refusal.
    fetchMock.mockImplementation(async () => ({
      ok: true,
      status: 200,
      json: async () => boardFixture,
    }));
    fireEvent.click(retry);

    expect(await screen.findByText('2 of 2 worker containers in use')).toBeTruthy();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});

/**
 * The review workspace on a card: the "Open workspace" action, the link and time it
 * answers with, and the factory's own refusal when it cannot open one. The board read
 * and the workspace post are the only two calls, and the latter's body is the
 * factory's own view — so every assertion here is about what a reader can read and
 * press, not about a value the page invented.
 */
describe('the review workspace on a card', () => {
  /** Answer the board read with the fixture, and the workspace post with a given view. */
  function answerBoardAndWorkspace(workspace: unknown) {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/workspace')) {
        return { ok: true, status: 200, json: async () => workspace };
      }
      return { ok: true, status: 200, json: async () => boardFixture };
    });
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  }

  const cardOf = (title: string) =>
    screen.getByText(title).closest('[data-slot="kanban-01-card"]') as HTMLElement;

  it('offers an open action on a card in Review and renders the link and time once opened', async () => {
    const fetchMock = answerBoardAndWorkspace({
      active: true,
      url: 'http://127.0.0.1:7100/',
      remainingSeconds: 14390,
      error: null,
    });

    render(<BoardPage />);

    await screen.findByText('Add the board endpoint');
    const card = cardOf('Add the board endpoint');
    fireEvent.click(within(card).getByRole('button', { name: 'Open workspace' }));

    // The link is the loopback address the factory published, and the time beside it
    // is the lifetime it reported, said in a reviewer's words.
    const link = await within(card).findByRole('link');
    expect(link.getAttribute('href')).toBe('http://127.0.0.1:7100/');
    expect(within(card).getByText('3h 59m remaining')).toBeTruthy();

    // The one call the action makes, to the factory's own route for the card's work item.
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/work-items/11111111-1111-1111-1111-111111111111/workspace',
      { method: 'POST' },
    );
  });

  it('renders a workspace the factory already reported, without pressing anything', async () => {
    answerWith(boardFixture);

    render(<BoardPage />);

    // The prism card carries an open workspace in the board the factory returned, so a
    // re-render shows the link and the time left rather than an action whose click was
    // forgotten.
    await screen.findByText('Tighten the focus ring');
    const card = cardOf('Tighten the focus ring');
    const link = within(card).getByRole('link');
    expect(link.getAttribute('href')).toBe('http://127.0.0.1:7101/');
    expect(within(card).getByText('3h 59m remaining')).toBeTruthy();
    expect(within(card).queryByRole('button', { name: 'Open workspace' })).toBeNull();
  });

  it("renders the factory's refusal on the card when no workspace can be opened", async () => {
    answerBoardAndWorkspace({
      active: false,
      url: null,
      remainingSeconds: null,
      error: 'the latest round left no tree to open',
    });

    render(<BoardPage />);

    await screen.findByText('Add the board endpoint');
    const card = cardOf('Add the board endpoint');
    fireEvent.click(within(card).getByRole('button', { name: 'Open workspace' }));

    // Never silently: the factory's own words are on the card, and the action stays so
    // the reviewer can try again.
    expect(await within(card).findByText('the latest round left no tree to open')).toBeTruthy();
    expect(within(card).getByRole('button', { name: 'Open workspace' })).toBeTruthy();
    expect(within(card).queryByRole('link')).toBeNull();
  });
});
