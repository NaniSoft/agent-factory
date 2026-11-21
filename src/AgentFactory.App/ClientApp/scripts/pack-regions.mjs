/**
 * This app's own region resolver.
 *
 * The pack-boundary gate requires one because naming a region means knowing
 * this app's own structure. The app is one ground: the document carries
 * `data-pack="lavender"`, and the only boundary beneath it is the product mark
 * the console shell draws at the head of its rail. That mark is the region this
 * app declares, and every other element resolves to null so a new boundary is
 * reported rather than silently inherited.
 *
 * @param {Element} element The element carrying `data-pack`.
 * @returns {string | null} The region's name, or null when the app does not name it.
 */
export function regionOf(element) {
  if (element.getAttribute('data-slot') === 'product-mark') {
    return 'product-mark';
  }
  return null;
}
