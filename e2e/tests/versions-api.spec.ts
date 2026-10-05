import { expect, test } from '@playwright/test';
import { ANTIFORGERY_HEADERS } from '../support/session.ts';

interface NextNumbers {
  options: { number: string; kind: 'sibling' | 'child'; proposed: boolean }[];
}

/**
 * The story has no screen: this checks, against the real image and its migrated database, that a
 * new Song's Version 1 offers `2` (proposed) and `1.1`, and that an unknown Version is 404.
 */
test.describe('Version numbers API', () => {
  test('proposes the next numbers for a branch point', async ({ page }) => {
    await page.goto('./songs');
    const base = new URL('.', page.url());
    const api = (path: string) => new URL(`api/v1/${path}`, base).toString();
    const request = page.request;

    const created = await request.post(api('songs'), {
      data: { title: `Branch point ${String(Date.now())}` },
      headers: ANTIFORGERY_HEADERS,
    });
    expect(created.status()).toBe(201);
    const song = (await created.json()) as { currentVersion: { id: string } };

    const response = await request.get(api(`versions/${song.currentVersion.id}/next-numbers`));
    expect(response.status()).toBe(200);
    expect((await response.json()) as NextNumbers).toEqual({
      options: [
        { number: '2', kind: 'sibling', proposed: true },
        { number: '1.1', kind: 'child', proposed: false },
      ],
    });

    const unknown = await request.get(
      api('versions/01a10a6e-0000-7000-8000-000000000000/next-numbers'),
    );
    expect(unknown.status()).toBe(404);
    expect(((await unknown.json()) as { code: string }).code).toBe('not_found');
  });
});
