# The board wears the console shell, and its main column is the scroller

The board is a working surface, not a site: a reader arrives to find out what the
fleet is doing and leaves when they know. Prism 1.10 made its panes glass and
added `AppShell01`, the frame its own admin showcase uses, so the board adopts
that frame and the release's material in one move. The rail holds the four
surfaces, the top bar holds the trail, the mode control and the fleet's pause
command, and the aurora wash arrives at `subtle` inside the content column only,
because a particle field behind an instrument panel is a distraction rather than
a setting.

The rail stays put and the main column scrolls. A console's rail is how a reader
leaves the page they are on, and a document that scrolls it away takes that with
it on exactly the long run tables a reader needs it. The Block lays the frame
out; one line in `app/globals.css` says which part moves.

The board's own ground is unchanged: `lavender`, dark by default, no pack
switcher and no second pack region. The rail's product mark is the only pack
boundary beneath the document, and it wears the ground.

## Considered Options

- **Keep `SiteNavbar` and `SiteFooter` and add a ground Block behind them**:
  rejected. That is a marketing frame on a working surface. `Ground01` is the
  page's continuous ground, with the particle field a landing wears and a bar
  that turns to glass over it; a console composes the wash itself, at the
  package's own `subtle` intensity, which is what the design system documents for
  exactly this case. The footer also came off: a board does not need a footer to
  say what it is when the rail already says it.
- **Put the wash behind the whole shell rather than the content column**:
  rejected. The shell's ground is opaque and the rail is a still surface; a light
  that crossed it would make navigation the figure. The wash is isolated behind
  the working column, and the rail keeps a quiet ground.
- **Let the document scroll with a sticky rail**: rejected as a half-measure.
  Sticky positioning inside the Block's own flex row keeps the rail's box on
  screen but leaves its height short of the viewport, so the rail's foot floats
  mid-page. The viewport-height shell with a scrolling main column keeps both the
  rail and the top bar where a console puts them.

## Consequences

- The run record keeps its own page inside the Runs route (`/runs?run=<id>`), and
  the rail marks Fleet while the top bar names Runs. A nested route is a fact the
  app's own `lib/site.ts` states rather than a second surface.
- The page header is composed in `components/page-header.tsx` rather than taken
  from `PageHeader01`, because that Block puts its trail and actions above the
  title and a console carries its trail in the bar.
- The board writes no Prism utility class and no material of its own. The glass
  is the pinned release's; this app's sheet carries layout, the rail's rows and
  the one scroll decision.
- The gates are unchanged in shape: one stylesheet, one ground, one pack boundary,
  and the pin moves with the package (1.7.0 to 1.10.0). The gate kit stays at
  generation 1 because the board is not a view-only consumer: it owns a
  stylesheet and a client runtime.
