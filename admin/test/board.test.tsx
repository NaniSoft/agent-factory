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
