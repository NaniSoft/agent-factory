import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import BoardPage from '@/app/page';
import boardFixture from './fixtures/board.json';

/**
 * The decisions on the Board, rendered the way the browser receives them, against a
 * fixture that mirrors the real `GET /api/board` response and a fetch that stands in
 * for the factory's own `/api/work-items/{id}/decisions`. The tests are about what a
 * reader can press and where: only the decisions the factory offers for a card's lane,
 * and the factory's own refusal rendered where the reviewer acted.
 */
const navigation = vi.hoisted(() => ({ search: new URLSearchParams() }));

vi.mock('next/navigation', () => ({
  useSearchParams: () => navigation.search,
}));

/** A board read on every GET, and a scripted decision answer on every POST. */
function factoryAnswering(decisionAnswer: unknown) {
  const calls: { method: string; url: string; body: string | null }[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : String(input);
    const method = init?.method ?? 'GET';
    calls.push({ method, url, body: typeof init?.body === 'string' ? init.body : null });

    if (method === 'POST') {
      return { ok: true, status: 200, json: async () => decisionAnswer };
    }
    return { ok: true, status: 200, json: async () => boardFixture };
  });
  vi.stubGlobal('fetch', fetchMock);
  return { fetchMock, calls };
}

/** The card element a title belongs to, read off the Block's own marker. */
function cardFor(title: string): HTMLElement {
  const card = screen.getByText(title).closest('[data-slot="kanban-01-card"]');
  if (card === null) throw new Error(`no card for ${title}`);
  return card as HTMLElement;
}

beforeEach(() => {
  navigation.search = new URLSearchParams();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the decisions on the board', () => {
  it('renders only the decisions the factory offers for a lane', async () => {
    factoryAnswering({ applied: true, refusal: null, resultingLane: 'Done' });

    render(<BoardPage />);
    await screen.findByText('Add the board endpoint');

    // A card in Review offers the workspace action (#47) and then all three decisions
    // (#45), in the factory's own order — the workspace first, as the Razor board
    // renders it. Both tickets' behaviour is on the card; neither is dropped.
    const review = cardFor('Add the board endpoint');
    expect(within(review).getAllByRole('button').map((button) => button.textContent)).toEqual([
      'Open workspace',
      'Approve',
      'Request changes',
      'Reject',
    ]);

    // A parked work item offers approve and reject only. Requesting changes is not
    // offered, because it is not a decision the store would keep about a parked item.
    const escalated = cardFor('Rework the intake rotation');
    expect(within(escalated).getAllByRole('button').map((button) => button.textContent)).toEqual([
      'Approve',
      'Reject',
    ]);
    expect(within(escalated).queryByRole('button', { name: 'Request changes' })).toBeNull();

    // A done work item offers nothing at all: no buttons, and no words for a brief.
    const done = cardFor('Ship the merger');
    expect(within(done).queryAllByRole('button')).toHaveLength(0);
    expect(within(done).queryByText('Why send it back?')).toBeNull();
  });

  it('shows the factory refusal when a request for changes has no words', async () => {
    const refusal =
      "requesting changes needs the reviewer's reasons: they are the next round's brief, "
      + 'and there is nothing here to hand it';
    const { calls } = factoryAnswering({ applied: false, refusal, resultingLane: 'Review' });

    render(<BoardPage />);
    await screen.findByText('Add the board endpoint');

    // The reviewer asks for changes and writes nothing. The form does not stop them:
    // the refusal is the store's, and the store's own words are what they are owed.
    const review = cardFor('Add the board endpoint');
    fireEvent.click(within(review).getByRole('button', { name: 'Request changes' }));

    // The factory was asked about the right work item, with the decision it posted.
    await waitFor(() => {
      expect(calls.some((call) => call.method === 'POST')).toBe(true);
    });
    const posted = calls.find((call) => call.method === 'POST');
    expect(posted?.url).toBe('/api/work-items/11111111-1111-1111-1111-111111111111/decisions');
    expect(posted?.body).toContain('"decision":"request-changes"');

    // And the refusal is rendered where the reviewer acted, not silently.
    expect(await screen.findByText(refusal)).toBeTruthy();
  });
});
