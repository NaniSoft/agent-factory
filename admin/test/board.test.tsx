import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import BoardPage from '@/app/page';
import boardFixture from './fixtures/board.json';

/**
 * The Board, rendered the way the browser receives it, against a fixture that
 * mirrors the real `GET /api/board` response.
 *
 * The tests are about what a reader can read and what the page does with the
 * factory's answer: the budget as the factory serialised it, the auto-merge mode,
 * and the two states a reader meets before the answer arrives. The fetch is the
 * only substituted thing; every Prism item is the real one from the pinned package.
 */
function answerWith(board: unknown, status = 200) {
  const fetchMock = vi.fn(async () => ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => board,
  }));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

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
