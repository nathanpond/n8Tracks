import axe from 'axe-core';
import { expect } from 'vitest';

/**
 * The unit-level accessibility check for the extension's own pages (popup, options): axe-core
 * over the document in jsdom. jsdom computes no layout or colours, so contrast is left to the
 * stylesheet's documented pairs; every structural rule (names, labels, roles, landmarks) runs.
 */
export async function expectNoAxeViolations(page: Document): Promise<void> {
  const results = await axe.run(page.documentElement, {
    rules: { 'color-contrast': { enabled: false } },
  });
  const violations = results.violations.map(
    (violation) =>
      `${violation.id}: ${violation.nodes.map((node) => node.target.join(' ')).join(', ')}`,
  );
  expect(violations).toEqual([]);
}

/** Loads an extension page's HTML into jsdom's document, keeping the `<html>` attributes (`lang`). */
export function loadPage(html: string): void {
  const parsed = new DOMParser().parseFromString(html, 'text/html');
  document.documentElement.replaceWith(document.importNode(parsed.documentElement, true));
}
