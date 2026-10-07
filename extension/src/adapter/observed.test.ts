import { describe, expect, it } from 'vitest';
import {
  isObservedMessage,
  isObserverReady,
  observedKindOf,
  observedRequestOf,
  OBSERVER_SOURCE,
  submittedOf,
  withoutSecrets,
} from './observed.ts';

const PAGE = 'https://suno.com/me';
const API = 'https://studio-api-prod.suno.com';

describe('which responses the observer forwards', () => {
  it.each([
    [`${API}/api/feed/v3`, 'POST', 'library-feed'],
    [`${API}/api/clips/trashed_v2?limit=20&cursor=x`, 'GET', 'trash'],
    [
      `${API}/api/project/me?page=2&sort=max_created_at_last_&show_trashed=false&exclude_shared=false`,
      'GET',
      'workspaces',
    ],
    [`${API}/api/playlist/me?page=1&show_trashed=false&show_sharelist=false`, 'GET', 'playlists'],
    [`${API}/api/unified/feed`, 'post', 'playlist-feed'],
    ['/api/feed/v3/', 'POST', 'library-feed'],
    // The answer to the user's own Create click (#149); the extension never clicks Create itself.
    [`${API}/api/generate/v2-web/`, 'POST', 'create'],
  ])('forwards %s (%s) as %s', (address, method, kind) => {
    expect(observedKindOf(address, method, PAGE)).toBe(kind);
  });

  it.each([
    // Only the Create itself: not a read of it, nor another generate path.
    [`${API}/api/generate/v2-web/`, 'GET'],
    [`${API}/api/generate/v2`, 'POST'],
    [`${API}/api/persona/get-personas/?page=1`, 'GET'],
    [`${API}/api/billing/info/`, 'GET'],
    [`${API}/api/download/authorize`, 'POST'],
    [`${API}/api/feed/v3`, 'GET'],
    [`${API}/api/feed/v3/extra`, 'POST'],
    ['http://studio-api-prod.suno.com/api/feed/v3', 'POST'],
    ['https://example.com/api/feed/v3', 'POST'],
    ['https://suno.com.example.com/api/feed/v3', 'POST'],
    ['https://cdn2.suno.ai/image_x.jpeg', 'GET'],
    ['::not an address', 'GET'],
  ])('leaves %s (%s) alone', (address, method) => {
    expect(observedKindOf(address, method, 'about:blank')).toBeNull();
  });
});

describe('what the observer keeps of a request', () => {
  it('keeps only the paging fields and the filters of the body', () => {
    const body = JSON.stringify({
      cursor: 'c2',
      limit: 20,
      filters: { user: { presence: 'True', userId: 'u' }, disliked: 'False' },
      token: 'anti-bot',
      create_session_token: 'session',
      user_tier: 'pro',
      prompt: 'lyrics never leave the page',
    });

    expect(observedRequestOf(`${API}/api/feed/v3`, PAGE, body)).toEqual({
      cursor: 'c2',
      page: null,
      filters: { user: { presence: 'True', userId: 'u' }, disliked: 'False' },
      feedId: null,
    });
  });

  it('reads the page number and cursor of a query, and a unified feed ID', () => {
    expect(observedRequestOf(`${API}/api/project/me?page=3`, PAGE, undefined)).toMatchObject({
      page: 3,
      cursor: null,
    });
    expect(
      observedRequestOf(`${API}/api/clips/trashed_v2?limit=20&cursor=abc`, PAGE, undefined),
    ).toMatchObject({ cursor: 'abc', page: null });
    expect(
      observedRequestOf(
        `${API}/api/unified/feed`,
        PAGE,
        JSON.stringify({ feed_id: 'generic_playlist:p1', cursor: null, page_size: 50 }),
      ),
    ).toEqual({ cursor: null, page: null, filters: null, feedId: 'generic_playlist:p1' });
  });

  it('reads a body that is not JSON text as empty', () => {
    expect(observedRequestOf(`${API}/api/feed/v3`, PAGE, 'not json')).toEqual({
      cursor: null,
      page: null,
      filters: null,
      feedId: null,
    });
    expect(observedRequestOf(`${API}/api/feed/v3`, PAGE, new Blob(['{}']))).toMatchObject({
      filters: null,
    });
  });

  it('leaves out the token, the session token, and the tier even inside the filters', () => {
    const request = observedRequestOf(
      `${API}/api/feed/v3`,
      PAGE,
      JSON.stringify({ filters: { token: 't', nested: [{ user_tier: 'pro', ok: 1 }] } }),
    );

    expect(request.filters).toEqual({ nested: [{ ok: 1 }] });
  });
});

describe('withoutSecrets', () => {
  it('drops the three names at any depth and keeps everything else', () => {
    expect(
      withoutSecrets({
        token: 'a',
        clips: [{ id: '1', create_session_token: 'b', metadata: { user_tier: 'c', tags: 'x' } }],
        n: 2,
      }),
    ).toEqual({ clips: [{ id: '1', metadata: { tags: 'x' } }], n: 2 });
    expect(withoutSecrets('token')).toBe('token');
    expect(withoutSecrets(null)).toBeNull();
  });
});

describe('what the observer keeps of a Create request (#149)', () => {
  it('keeps the values at the createRequest paths, nested as sent, and leaves out everything else', () => {
    const body = JSON.stringify({
      token: 'anti-bot',
      title: 'A title',
      tags: 'dream pop',
      prompt: 'lyrics',
      mv: 'chirp-goose',
      duration: 30,
      user_uploaded_images_b64: ['aGVsbG8='],
      transaction_uuid: 'tx',
      lyrics_project_id: 'lp',
      metadata: {
        create_mode: 'custom',
        user_tier: 'tier-secret',
        create_session_token: 'session-secret',
        web_client_pathname: '/create',
        vocal_gender: 'f',
        control_sliders: { weirdness_constraint: 0.7, other: 1 },
      },
    });

    expect(submittedOf(body)).toEqual({
      title: 'A title',
      tags: 'dream pop',
      prompt: 'lyrics',
      mv: 'chirp-goose',
      duration: 30,
      metadata: {
        create_mode: 'custom',
        vocal_gender: 'f',
        control_sliders: { weirdness_constraint: 0.7 },
      },
    });
    const kept = JSON.stringify(submittedOf(body));
    for (const secret of [
      'token',
      'create_session_token',
      'user_tier',
      'anti-bot',
      'session-secret',
      'tier-secret',
    ]) {
      expect(kept).not.toContain(secret);
    }
  });

  it('reads a body that is not JSON text of an object as unread', () => {
    expect(submittedOf(undefined)).toBeNull();
    expect(submittedOf('not json')).toBeNull();
    expect(submittedOf('[1]')).toBeNull();
    expect(submittedOf(new Blob(['{}']))).toBeNull();
    // Complement: an object with none of the paths is read, and empty.
    expect(submittedOf('{"token":"t"}')).toEqual({});
  });
});

describe('the messages between the observer and the content script', () => {
  it('recognises an observed response, and nothing else', () => {
    const message = {
      source: OBSERVER_SOURCE,
      type: 'observed',
      kind: 'trash',
      request: { cursor: null, page: null, filters: null, feedId: null },
      body: {},
    };

    expect(isObservedMessage(message)).toBe(true);
    expect(isObservedMessage({ ...message, kind: 'create' })).toBe(true);
    expect(isObservedMessage({ ...message, kind: 'download' })).toBe(false);
    expect(isObservedMessage({ ...message, source: 'suno' })).toBe(false);
    expect(isObservedMessage({ ...message, request: null })).toBe(false);
    expect(isObserverReady({ source: 'n8tracks-suno-content', type: 'observer-ready' })).toBe(true);
    expect(isObserverReady({ source: OBSERVER_SOURCE, type: 'observer-ready' })).toBe(false);
  });
});
