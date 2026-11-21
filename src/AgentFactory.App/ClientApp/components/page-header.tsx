import type { ReactNode } from 'react';

import { Separator } from '@nanisoft/prism-ui/components/separator';
import { Heading, Text } from '@nanisoft/prism-ui/components/typography';

/**
 * The board's page header: the claim, the supporting sentence and the page's
 * controls, on one line where the width allows it.
 *
 * **It is composed rather than taken from `PageHeader01`.** That Block puts its
 * trail and its actions in a row above the title, which is the right shape for
 * a page reached through a marketing trail: the trail is the way back and the
 * actions are the page's offer. A console already carries its trail in the top
 * bar, so the row above the title would hold nothing but a right-aligned
 * cluster, and the reader would meet the controls before the name of the thing
 * they control. Here the heading leads, the controls settle beside it, and the
 * rule under both is the same hairline the Block draws.
 *
 * The type is Prism's: `Heading` is the ladder's `3xl` step and `Text` its muted
 * body, so a board title and a board sentence are the sizes the system authored
 * rather than this app's approximations of them.
 */
export function PageHeader({
  title,
  description,
  actions,
}: {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <div className="app-page-head">
      <div className="app-page-head__row">
        <div className="app-page-head__text">
          <Heading as="h1" size="3xl">
            {title}
          </Heading>
          {description === undefined ? null : (
            <Text tone="muted" className="app-page-head__lede">
              {description}
            </Text>
          )}
        </div>
        {actions === undefined ? null : <div className="app-actions">{actions}</div>}
      </div>
      <Separator />
    </div>
  );
}
