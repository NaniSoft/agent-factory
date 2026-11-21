/**
 * The board's own facts, in one module.
 *
 * Three decisions live here and nowhere else: the pack the board sits on, the
 * mode a reader who has never chosen gets, and the one product the chrome draws
 * its mark for. The shell, the document and the gate map all read them, so a
 * board that changed its ground would change it in one place rather than in a
 * layout and a JSON file that disagree.
 *
 * The pack is Prism's own vocabulary, typed by `PackId`, so a mistyped id is a
 * compile error rather than an attribute that matches no emitted rule and
 * silently inherits the ground.
 */
import { themeAttributes, type Mode, type PackId } from '@nanisoft/prism-ui/theming';

/**
 * The ground. The Agent Factory is one surface: lavender, dark by default.
 *
 * A working surface keeps one ground for the life of the page, and a control
 * that repainted it would be offering a reader a board that is not this one.
 * The mode is the axis that belongs to the reader, and the shell's toggle is
 * the one theme control the bar carries.
 */
export const GROUND_PACK: PackId = 'lavender';

/** What a reader who has never chosen sees. */
export const DEFAULT_MODE: Mode = 'dark';

/**
 * The two attributes that carry the theme, spread onto the document element.
 *
 * The declarative form is the whole runtime: the boot script applies a stored
 * choice to these attributes before first paint, and no provider needs to sit
 * above the page for the palette to resolve.
 */
export const THEME_ATTRIBUTES = themeAttributes({ pack: GROUND_PACK, mode: DEFAULT_MODE });

/** The product the rail and the footer of the shell sign with. */
export const PRODUCT = {
  id: 'agent-factory',
  name: 'Agent Factory',
  pack: GROUND_PACK,
} as const;

/**
 * The board's four surfaces, in the order the fleet is read: what is happening,
 * how it is arranged, where it happens, and who does the work.
 */
export const SURFACES = [
  { label: 'Fleet', href: '/' },
  { label: 'Pipelines', href: '/pipelines' },
  { label: 'Targets', href: '/targets' },
  { label: 'Profiles', href: '/profiles' },
] as const;

export type Surface = (typeof SURFACES)[number];

/**
 * The routes that hang under a surface rather than taking a rail row.
 *
 * `runs` is the fleet's own history: it is reached from the fleet and from a
 * run's row, so the rail keeps pointing at Fleet while the reader is in it, and
 * the trail still names the page they are actually reading.
 */
const NESTED: readonly { href: string; label: string; rail: (typeof SURFACES)[number]['href'] }[] = [
  { href: '/runs', label: 'Runs', rail: '/' },
];

/** The surface a pathname serves, or the fleet for a route outside the set. */
export function surfaceOf(pathname: string): Surface {
  const here = pathname.replace(/\/+$/, '') || '/';
  return SURFACES.find((surface) => surface.href === here) ?? SURFACES[0];
}

/** The rail row that stays current on a pathname, following nested routes. */
export function railSurfaceOf(pathname: string): Surface {
  const here = pathname.replace(/\/+$/, '') || '/';
  const nested = NESTED.find((entry) => entry.href === here);
  if (nested !== undefined) {
    return SURFACES.find((surface) => surface.href === nested.rail) ?? SURFACES[0];
  }
  return surfaceOf(here);
}

/** The page a pathname names, for the trail above the content. */
export function pageLabelOf(pathname: string): string {
  const here = pathname.replace(/\/+$/, '') || '/';
  return NESTED.find((entry) => entry.href === here)?.label ?? surfaceOf(here).label;
}
