/**
 * The page the static export emits for an address it does not serve.
 *
 * It renders inside the root layout, so it carries the same ground and mode as the
 * Board, and it offers the one address this app has: the Board itself, off this
 * app's base path.
 */
export default function NotFound() {
  return (
    <main className="page">
      <header className="page__header">
        <h1 className="page__title">There is no page here</h1>
        <p className="page__lede">The Board is at the root of this app.</p>
      </header>
    </main>
  );
}
