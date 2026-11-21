import type { Metadata } from 'next';
import type { ReactNode } from 'react';

import { PrismThemeScript } from '@nanisoft/prism-ui/provider';

import { SiteChrome } from '@/components/site-chrome';
import { DEFAULT_MODE, GROUND_PACK, THEME_ATTRIBUTES } from '@/lib/site';

// The one stylesheet: every token, utility and base rule arrives with it, and
// this app's own sheet follows because the design system's base is layered and
// the app's is not.
import '@nanisoft/prism-ui/styles.css';
import './globals.css';

export const metadata: Metadata = {
  title: {
    default: 'Agent Factory',
    template: '%s · Agent Factory',
  },
  description:
    'The Agent Factory board: the fleet, its runs, pipelines, targets and agent profiles.',
};

/**
 * The document, and nothing else.
 *
 * **The theme is two attributes plus one blocking script.** `themeAttributes()`
 * returns the pack as `data-pack` and the mode as the document's `className`, so
 * the server render and a reader who has chosen nothing agree, and a stored
 * choice is applied before first paint. The element carries the spread and no
 * second `className`, because a second one would replace the mode class or be
 * replaced by it.
 *
 * **There is no provider here.** The shell mounts one around the two controls it
 * composes, which is the only place a provider is needed: it is the one writer
 * of the attributes the boot script reads, and every other surface resolves its
 * colours through the cascade.
 */
export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en" {...THEME_ATTRIBUTES} suppressHydrationWarning>
      <head>
        <PrismThemeScript defaultPack={GROUND_PACK} defaultMode={DEFAULT_MODE} />
      </head>
      <body>
        <SiteChrome>{children}</SiteChrome>
      </body>
    </html>
  );
}
