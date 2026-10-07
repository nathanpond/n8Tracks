/**
 * Finds an element of the extension's own pages (the popup and the options page) by ID. Kept out
 * of the views the Suno panel shares, so nothing built into the Suno content script can query
 * Suno's page (the invariant 4 guard's static scan).
 */
export function element(page: Document, id: string): HTMLElement {
  const found = page.getElementById(id);
  if (found === null) {
    throw new Error(`The page has no #${id} element.`);
  }
  return found;
}
