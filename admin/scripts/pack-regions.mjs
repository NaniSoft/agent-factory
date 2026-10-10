/**
 * This app's own region resolver.
 *
 * The pack-boundary gate requires one, because naming a region means knowing this
 * app's own structure, and a table of regions in the gate would be the gate's
 * guess at a page it does not own. A resolver the gate cannot find is a coverage
 * failure rather than a pass: a boundary it could not name would otherwise be a
 * boundary it could not hold to the map.
 *
 * There is nothing to name yet. The Board is one ground and no element beneath the
 * document carries a pack of its own, so this returns null for every element. That
 * is deliberately a refusal rather than a default: a boundary that appeared
 * without a region being declared here would be reported as unnamed, which is the
 * finding a reader needs — an unnameable boundary cannot be held to a map.
 *
 * @param {Element} element The element carrying `data-pack`.
 * @returns {string | null} The region's name, or null when the app does not name it.
 */
export function regionOf(element) {
  void element;
  return null;
}
