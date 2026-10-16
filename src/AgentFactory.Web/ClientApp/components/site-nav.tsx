'use client';

import { usePathname } from 'next/navigation';

import { CtaLink } from '@nanisoft/prism-ui/components/cta-link';

/**
 * The four surfaces, in the order a reader meets them. The board is the home route,
 * and the two configuration surfaces — the repositories the factory serves and the
 * keys it may use — are named for what a reader goes there to do.
 */
const LINKS: ReadonlyArray<{ href: string; label: string }> = [
  { href: '/', label: 'Board' },
  { href: '/work-items', label: 'Work items' },
  { href: '/projects', label: 'Projects' },
  { href: '/credentials', label: 'Credentials' },
];

/**
 * The one navigation for the whole Board app.
 *
 * The four routes are otherwise islands: each page renders its own `main` and no
 * page links to another except a work item's back link, so a reader on the board
 * had no way to discover that the Projects and Credentials surfaces exist. This
 * sits above every page, in the layout, so the routes are reachable from all of
 * them.
 *
 * The links are Prism `CtaLink`s — native anchors, so the browser's own link
 * affordances work — and the page a reader is on carries `aria-current="page"`
 * beside a heavier variant, so the active state is stated in the markup rather
 * than authored by the site's stylesheet. The trailing slash the export navigates
 * with is normalised away before the comparison; `usePathname` carries no query,
 * so a filtered board still reads as the board.
 */
export function SiteNav() {
  const pathname = usePathname();
  const here = pathname.replace(/\/+$/, '') || '/';

  return (
    <nav className="site-nav" aria-label="The factory Board">
      <div className="site-nav__row">
        {LINKS.map(({ href, label }) => {
          const current = href === here;
          return (
            <CtaLink
              key={href}
              href={href}
              variant={current ? 'secondary' : 'ghost'}
              size="sm"
              aria-current={current ? 'page' : undefined}
            >
              {label}
            </CtaLink>
          );
        })}
      </div>
    </nav>
  );
}
