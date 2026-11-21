'use client';

import { usePathname } from 'next/navigation';
import { useCallback, useEffect, useState } from 'react';

import { AppShell01 } from '@nanisoft/prism-ui/blocks/app-shell-01';
import { AuroraWash } from '@nanisoft/prism-ui/components/aurora-wash';
import { Badge } from '@nanisoft/prism-ui/components/badge';
import { Button } from '@nanisoft/prism-ui/components/button';
import { ProductMark } from '@nanisoft/prism-ui/components/product-mark';

import { api } from '@/lib/api';
import { DEFAULT_MODE, GROUND_PACK, PRODUCT, SURFACES, pageLabelOf, railSurfaceOf } from '@/lib/site';
import type { Fleet } from '@/lib/types';

/**
 * The console every page renders: a rail that is always in reach, a top bar that
 * names where the reader is, and the board's one ground.
 *
 * **The rail is Prism's `AppShell01`, composed rather than imitated.** The
 * frame owns the breakpoint behaviour, the mobile sheet, the breadcrumb trail
 * and the two theme controls; this file supplies only what the board owns: its
 * four surfaces, its mark, the fleet's kill switch and the labels. Below `lg`
 * the same navigation arrives as a sheet, so no destination is ever only a
 * horizontal menu that a phone clips.
 *
 * **The ground is the wash and nothing else.** A console's working surface does
 * not wear the marketing `Ground01` Block: a fixed particle field behind an
 * instrument panel is a distraction rather than a setting. It composes the same
 * aurora wash the marketing side wears, at the package's `subtle` intensity, in
 * an isolated layer behind the content column only, so the rail keeps a still
 * ground and the glass panes have something to be translucent over.
 *
 * **The bar carries no colour chooser.** The board's ground is a fact about the
 * board rather than a choice a reader makes on it, so the mode is the one theme
 * axis in the bar and the pack stays lavender everywhere.
 */
export function SiteChrome({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const current = railSurfaceOf(pathname);
  const page = pageLabelOf(pathname);

  const navigation = (
    <ul className="app-rail">
      {SURFACES.map((surface) => {
        const isCurrent = surface.href === current.href;
        return (
          <li key={surface.href}>
            <a
              href={surface.href}
              className={isCurrent ? 'app-rail__link app-rail__link--current' : 'app-rail__link'}
              aria-current={isCurrent ? 'page' : undefined}
            >
              {surface.label}
            </a>
          </li>
        );
      })}
    </ul>
  );

  return (
    <div className="app-shell">
      <AppShell01
        navigation={navigation}
        navigationLabel="Agent Factory surfaces"
        brand={<ProductMark id={PRODUCT.id} name={PRODUCT.name} pack={PRODUCT.pack} />}
        breadcrumbs={[{ label: page }]}
        mode={{ lightLabel: 'Switch to dark mode', darkLabel: 'Switch to light mode' }}
        defaultPack={GROUND_PACK}
        defaultMode={DEFAULT_MODE}
        actions={<FleetPauseControl />}
        railFooter={
          <p className="app-rail__foot">
            Built with <a href="https://prism.nanisoft.com">Prism</a>
          </p>
        }
      >
        <div className="app-workspace">
          <AuroraWash intensity="subtle" className="app-workspace__wash" />
          {children}
        </div>
      </AppShell01>
    </div>
  );
}

/**
 * The fleet-wide kill switch. It sits in the bar because it is the one command
 * that is not about the page a reader is on: pause stops dequeueing, lets
 * in-flight runs finish and leaves scanning alone.
 *
 * The badge names the state and the button names the action, so a reader who
 * cannot see the badge still hears the state in the button's word. Below the
 * width the top bar can carry both, the badge steps out and the button stays:
 * the control is the command, and a command is the half that must not be lost.
 */
function FleetPauseControl() {
  const [fleet, setFleet] = useState<Fleet | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let live = true;
    const read = () => {
      api
        .fleet()
        .then((answer) => {
          if (live) setFleet(answer);
        })
        .catch(() => {
          // The page itself says when the factory is unreachable; the bar does
          // not repeat it.
        });
    };
    void read();
    const interval = window.setInterval(read, 15000);
    return () => {
      live = false;
      window.clearInterval(interval);
    };
  }, []);

  const toggle = useCallback(() => {
    if (fleet === null) return;
    setBusy(true);
    api
      .setPaused(!fleet.paused)
      .then((answer) =>
        setFleet((current) =>
          current === null ? current : { ...current, paused: answer.paused },
        ),
      )
      .catch(() => undefined)
      .finally(() => setBusy(false));
  }, [fleet]);

  if (fleet === null) {
    return null;
  }

  return (
    <div className="app-pause">
      <Badge
        variant={fleet.paused ? 'warning' : 'success'}
        className="app-pause__badge"
      >
        {fleet.paused ? 'fleet paused' : 'fleet running'}
      </Badge>
      <Button
        type="button"
        size="sm"
        variant={fleet.paused ? 'default' : 'outline'}
        onClick={toggle}
        disabled={busy}
      >
        {busy ? 'Working' : fleet.paused ? 'Resume' : 'Pause'}
      </Button>
    </div>
  );
}
