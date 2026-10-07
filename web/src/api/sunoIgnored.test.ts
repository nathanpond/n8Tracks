import { describe, expect, it } from 'vitest';
import { jsonResponse, requestPath, stubFetch } from '../test/helpers';
import { ignoreSource } from './sunoIgnored';

describe('putting a Not imported source on the ignore list', () => {
  it('sends its Suno ID and reads each answer', async () => {
    const mock = stubFetch();
    const answers = [
      jsonResponse(200, { sunoId: 'x', added: true }),
      jsonResponse(200, { sunoId: 'x', added: false }),
      jsonResponse(409, { code: 'already_imported' }),
      jsonResponse(409, { code: 'tombstoned' }),
      jsonResponse(404, { code: 'not_found' }),
    ];
    mock.mockImplementation(() => Promise.resolve(answers.shift() ?? jsonResponse(500, {})));

    const results = [];
    for (let index = 0; index < 5; index++) {
      results.push((await ignoreSource('x')).kind);
    }

    expect(results).toEqual(['added', 'already-listed', 'imported', 'deleted', 'failed']);
    const [input, init] = mock.mock.calls[0] ?? [];
    expect(input === undefined ? '' : requestPath(input)).toMatch(/api\/v1\/suno\/ignored$/);
    expect(init?.method).toBe('POST');
    expect(init?.body).toBe(JSON.stringify({ sunoId: 'x' }));
  });
});
