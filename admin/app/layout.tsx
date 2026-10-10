import type { Metadata } from 'next';
import type { ReactNode } from 'react';

import { PrismProvider, PrismThemeScript } from '@nanisoft/prism-ui/provider';

// The one stylesheet. Every token, every utility and every base rule arrive in
// this single import; the app adds its own handful of classes after it and nothing
// else. Imported before `globals.css`, because the design system's base is layered
// and the app's sheet is not.
import '@nanisoft/prism-ui/styles.css';
import './globals.css';

export const metadata: Metadata = {
  title: 'The factory board',
  description:
    'The agent factory\u2019s Board: how many worker containers it is inside, and whether silence can merge.',
};

/**
 * The document.
 *
 * The Board wears lavender and is dark by default, stated twice: as the two
 * attributes the design system's own declarative form reads (`data-pack` and
 * `.dark`) and as the defaults handed to the provider, so a server render and a
 * reader who has chosen nothing agree. `PrismThemeScript` applies a stored choice
 * before first paint and is the only writer of the theme origin.
 *
 * The provider is mounted once here and never per page: it is the optional client
 * runtime, and a second one anywhere would be a second owner of the same two
 * attributes.
 */
export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en" data-pack="lavender" className="dark" suppressHydrationWarning>
      <head>
        <PrismThemeScript defaultPack="lavender" defaultMode="dark" />
      </head>
      <body>
        <PrismProvider defaultPack="lavender" defaultMode="dark">
          {children}
        </PrismProvider>
      </body>
    </html>
  );
}
