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
 * the project filter narrowing the cards, the auto-merge mode, and
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
  autoMerge: false,
  lanes: [
    { lane: 'Backlog', label: 'Backlog', cards: [] },
    { lane: 'Frontier', label: 'Frontier', cards: [] },
    { lane: 'InProgress', label: 'In Progress', cards: [] },
    { lane: 'Review', label: 'Review', cards: [] },
    { lane: 'Done', label: 'Done', cards: [] },
    { lane: 'Waiting', label: 'Waiting', cards: [] },
    { lane: 'Escalated', label: 'Escalated', cards: [] },
    { lane: 'Rejected', label: 'Rejected', cards: [] },
  ],
  projects: [],
  filterProjects: [],
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
  it('renders the auto-merge mode the factory returned', async () => {
    const fetchMock = answerWith(boardFixture);

    render(<BoardPage />);

    // The mode in the factory's own words, off the fixture rather than a string
    // the page composed on its own.
    expect(await screen.findByText('auto-merge is on')).toBeTruthy();

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
    // could be reworded. The factory renders the six swimlanes — Waiting among them,
    // after Done — and then the two endings, Escalated and Rejected: eight columns,
    // and the park/final ones last.
    const lanes = [
      'Backlog',
      'Frontier',
      'InProgress',
      'Review',
      'Done',
      'Waiting',
      'Escalated',
      'Rejected',
    ];
    for (const lane of lanes) {
      expect(document.querySelector(`[data-column="${lane}"]`)).toBeTruthy();
    }
    expect(document.querySelectorAll('[data-column]').length).toBe(8);
    expect([...document.querySelectorAll('[data-column]')].map((column) => column.getAttribute('data-column'))).toEqual(lanes);

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

  it('says when a Review card will merge if nobody decides', async () => {
    answerWith(boardFixture);

    render(<BoardPage />);
    await screen.findByText('Add the board endpoint');

    // The per-card countdown is the factory's own moment, said on the card rather than only
    // in the strip, so a reviewer can act on the work item while it still matters.
    const card = screen
      .getByText('Add the board endpoint')
      .closest('[data-slot="kanban-01-card"]') as HTMLElement;
    const line = within(card).getByText(/auto-merges at/);
    expect(line.textContent).toContain('2026-10-03 10:30 UTC');
    expect(within(card).getByText(/unless a reviewer decides first/)).toBeTruthy();
  });

  it('offers the acceptance gate where the factory says a card may be accepted, and posts it', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/accept')) {
        return { ok: true, status: 200, json: async () => ({ applied: true, refusal: null }) };
      }
      return { ok: true, status: 200, json: async () => boardFixture };
    });
    vi.stubGlobal('fetch', fetchMock);

    render(<BoardPage />);
    await screen.findByText('Add a fresh intake card');

    // The gate is on a Backlog card the factory judged a build ticket, and the click posts
    // to the loop's own accept endpoint.
    const backlog = screen
      .getByText('Add a fresh intake card')
      .closest('[data-slot="kanban-01-card"]') as HTMLElement;
    fireEvent.click(within(backlog).getByRole('button', { name: 'Accept into build' }));

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/work-items/55555555-5555-5555-5555-555555555555/accept',
      { method: 'POST' },
    );

    // Acceptance is gated by the factory's `offersAcceptance`, not by the lane alone: an
    // Unrouted Backlog item is in Backlog and is nevertheless offered no build button, and
    // a routed-Build item is. A card already in the build is offered none either.
    const unrouted = screen
      .getByText('Route this before it can be built')
      .closest('[data-slot="kanban-01-card"]') as HTMLElement;
    expect(within(unrouted).queryByRole('button', { name: 'Accept into build' })).toBeNull();

    const review = screen
      .getByText('Add the board endpoint')
      .closest('[data-slot="kanban-01-card"]') as HTMLElement;
    expect(within(review).queryByRole('button', { name: 'Accept into build' })).toBeNull();
  });

  it('renders a card in the Waiting lane, in the factory order, offering no decisions', async () => {
    answerWith(boardFixture);

    render(<BoardPage />);

    // Waiting is a column of its own, between Done and the two endings, and its cards are
    // drawn there.
    await screen.findByText('Decide the sync strategy');
    const waiting = document.querySelector('[data-column="Waiting"]');
    expect(waiting).not.toBeNull();
    expect(within(waiting as HTMLElement).getByText('Decide the sync strategy')).toBeTruthy();

    const order = [...document.querySelectorAll('[data-column]')].map((column) =>
      column.getAttribute('data-column'),
    );
    expect(order.indexOf('Waiting')).toBe(order.indexOf('Done') + 1);
    expect(order.indexOf('Waiting')).toBe(order.indexOf('Escalated') - 1);

    // Waiting is not a lane a reviewer can decide about, so its card carries no decision
    // buttons and no acceptance gate, however it got there.
    const card = within(waiting as HTMLElement)
      .getByText('Decide the sync strategy')
      .closest('[data-slot="kanban-01-card"]') as HTMLElement;
    expect(within(card).queryByRole('button', { name: 'Approve' })).toBeNull();
    expect(within(card).queryByRole('button', { name: 'Reject' })).toBeNull();
    expect(within(card).queryByRole('button', { name: 'Accept into build' })).toBeNull();
  });

  it('shows a card its route: the kind and the factory\'s own reason', async () => {
    answerWith(boardFixture);

    render(<BoardPage />);
    await screen.findByText('Decide the sync strategy');

    // The routing verdict is on the card: the kind's words and the one sentence the factory
    // wrote for it, so "nobody has looked at this yet" cannot read as "ready to build".
    const card = screen
      .getByText('Decide the sync strategy')
      .closest('[data-slot="kanban-01-card"]') as HTMLElement;
    const route = card.querySelector('[data-route]');
    expect(route).not.toBeNull();
    expect(route?.getAttribute('data-route')).toBe('Grill');
    expect(within(card).getByText('needs grilling')).toBeTruthy();
    expect(within(card).getByText(/it needs deciding with a person before it can be built/)).toBeTruthy();

    // A verdict that filed an issue points at it, so a reviewer can reach the thing the
    // factory is waiting on.
    expect(card.querySelector('[data-routing-issue="99"]')).not.toBeNull();

    // An unrouted card says plainly that nobody has looked at it yet.
    const unrouted = screen
      .getByText('Route this before it can be built')
      .closest('[data-slot="kanban-01-card"]') as HTMLElement;
    expect(unrouted.querySelector('[data-route]')?.getAttribute('data-route')).toBe('Unrouted');
    expect(within(unrouted).getByText('Unrouted')).toBeTruthy();
    expect(within(unrouted).getByText(/Nobody has looked at this issue yet/)).toBeTruthy();
  });

  it("links each card to its work item's own page", async () => {
    answerWith(boardFixture);

    render(<BoardPage />);

    // The card is the way into the detail: every round the work item has run, the change
    // each left, and its decisions. The id travels in the query string, because a static
    // export cannot emit an unbounded dynamic segment.
    await screen.findByText('Add the board endpoint');
    const card = screen
      .getByText('Add the board endpoint')
      .closest('[data-slot="kanban-01-card"]') as HTMLElement;
    const link = within(card).getByRole('link', { name: 'Open the work item' });
    expect(link.getAttribute('href')).toBe(
      '/work-items?item=11111111-1111-1111-1111-111111111111',
    );
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
    expect(screen.queryByText(/worker containers in use/)).toBeNull();
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

    expect(await screen.findByText('auto-merge is on')).toBeTruthy();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});

