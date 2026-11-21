import { EmptyState01 } from '@nanisoft/prism-ui/blocks/empty-state-01';
import { CtaLink } from '@nanisoft/prism-ui/components/cta-link';

import { PageHeader } from '@/components/page-header';

export default function NotFound() {
  return (
    <div className="app-page">
      <PageHeader
        title="Not found"
        description="There is no page at this address. The fleet, its runs and its configuration are one click away."
      />
      <EmptyState01
        reason="first-run"
        title="Nothing at this address"
        body="The route does not exist, or a run id no longer does. The board keeps history, so the run may still be in the table."
      />
      <div>
        <CtaLink href="/" variant="outline" size="sm">
          Back to the fleet
        </CtaLink>
      </div>
    </div>
  );
}
