import { describe, expect, it } from 'vitest';
import { jsonResponse, stubFetch } from '../test/helpers';
import {
  createSaveQueue,
  differingFields,
  isRetryable,
  patchWithRevision,
  type ComparedField,
  type FailureReason,
} from './saves';

interface Item {
  revision: number;
  title: string;
  concept: string | null;
}

const fields: ComparedField<Item>[] = [
  { key: 'title', label: 'Title', read: (item) => item.title },
  { key: 'concept', label: 'Concept', read: (item) => item.concept },
];

const accept = (answer: unknown) =>
  typeof answer === 'object' && answer !== null && 'revision' in answer
    ? (answer as Item)
    : undefined;

describe('differingFields', () => {
  const base: Item = { revision: 1, title: 'A', concept: null };

  it('compares the loaded record with the current one, field by field', () => {
    const current = { revision: 2, title: 'B', concept: null };
    expect(differingFields(fields, base, current, { concept: 'x' }).map((f) => f.key)).toEqual([
      'title',
    ]);
  });

  it('does not count a field both sides changed to the same value', () => {
    const current = { revision: 2, title: 'A', concept: 'x' };
    expect(differingFields(fields, base, current, { concept: 'x' })).toEqual([]);
    expect(differingFields(fields, base, current, { concept: 'y' }).map((f) => f.key)).toEqual([
      'concept',
    ]);
  });
});

describe('createSaveQueue', () => {
  it('runs each task after the one before it, even when that one fails', async () => {
    const enqueue = createSaveQueue();
    const order: string[] = [];
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const first = enqueue(async () => {
      await gate;
      order.push('first');
    });
    const failing = enqueue(() => {
      order.push('failing');
      return Promise.reject(new Error('refused'));
    });
    const last = enqueue(() => {
      order.push('last');
      return Promise.resolve('done');
    });

    release();
    await first;
    await expect(failing).rejects.toThrow('refused');
    await expect(last).resolves.toBe('done');
    expect(order).toEqual(['first', 'failing', 'last']);
  });
});

describe('patchWithRevision', () => {
  it('sends If-Match and reads saved, conflict, invalid, and failed answers', async () => {
    const mock = stubFetch();
    const answers = [
      jsonResponse(200, { revision: 2, title: 'B', concept: null }),
      jsonResponse(409, { code: 'revision_conflict', current: { revision: 3, title: 'C' } }),
      jsonResponse(422, { code: 'validation_failed', errors: { title: ['Enter a title.'] } }),
      jsonResponse(409, { code: 'credential_revoked' }),
      jsonResponse(428, { code: 'revision_required' }),
    ];
    mock.mockImplementation(() => Promise.resolve(answers.shift() ?? jsonResponse(500, {})));

    expect(await patchWithRevision('api/v1/items/1', 1, { title: 'B' }, accept)).toEqual({
      kind: 'saved',
      record: { revision: 2, title: 'B', concept: null },
    });
    const init = mock.mock.calls[0]?.[1];
    expect(new Headers(init?.headers).get('If-Match')).toBe('"1"');
    expect(init?.method).toBe('PATCH');
    expect(init?.body).toBe('{"title":"B"}');

    expect((await patchWithRevision('api/v1/items/1', 1, {}, accept)).kind).toBe('conflict');
    expect(await patchWithRevision('api/v1/items/1', 1, {}, accept)).toEqual({
      kind: 'invalid',
      errors: { title: ['Enter a title.'] },
    });
    expect((await patchWithRevision('api/v1/items/1', 1, {}, accept)).kind).toBe('failed');
    expect((await patchWithRevision('api/v1/items/1', 1, {}, accept)).kind).toBe('failed');
  });

  it('says why a write failed, and which failures may go through if sent again', async () => {
    const mock = stubFetch();
    const answers: (() => Promise<Response>)[] = [
      () => Promise.reject(new TypeError('Failed to fetch')),
      () => Promise.resolve(jsonResponse(503, { code: 'unavailable' })),
      () => Promise.resolve(jsonResponse(200, { unexpected: true })),
      () => Promise.resolve(jsonResponse(401, { code: 'not_authenticated' })),
      () => Promise.resolve(jsonResponse(409, { code: 'version_frozen' })),
      () => Promise.resolve(jsonResponse(404, { code: 'not_found' })),
      () => Promise.resolve(jsonResponse(403, { code: 'antiforgery_required' })),
      () => Promise.resolve(jsonResponse(429, { code: 'too_many_requests' })),
    ];
    mock.mockImplementation(() => (answers.shift() ?? (() => Promise.reject(new Error())))());

    const reasons: (FailureReason | undefined)[] = [];
    for (let i = 0; i < 8; i++) {
      const result = await patchWithRevision('api/v1/items/1', 1, {}, accept);
      reasons.push(result.kind === 'failed' ? result.reason : undefined);
    }
    expect(reasons).toEqual([
      'unreachable',
      'server',
      'server',
      'signed-out',
      'frozen',
      'gone',
      'refused',
      'server',
    ]);
    expect(reasons.map((reason) => isRetryable(reason))).toEqual([
      true,
      true,
      true,
      false,
      false,
      false,
      false,
      true,
    ]);
  });
});
