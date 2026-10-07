import { describe, expect, it, vi } from 'vitest';
import { CONTENT_SOURCE, OBSERVER_SOURCE, type ObservedMessage } from '../adapter/observed.ts';
import { jsonResponse } from '../testing/fakeBrowser.ts';
import { sunoFixture } from '../testing/sunoResponses.ts';
import { installObserver, type ObservedWindow } from './observe.ts';

const API = 'https://studio-api-prod.suno.com';
const ORIGIN = 'https://suno.com';

/** The secrets TS-003 found in the Create request; the observer must never pass one on. */
const SECRETS = ['token', 'create_session_token', 'user_tier'];

/** Suno's page, standing in for `window`: its own fetch answers from the fixtures. */
function suno(answer: (address: string) => Response) {
  const posted: { message: ObservedMessage; origin: string }[] = [];
  const listeners: ((event: MessageEvent) => void)[] = [];
  const pageFetch = vi.fn<typeof fetch>((input) =>
    Promise.resolve(
      answer(typeof input === 'string' ? input : 'href' in input ? input.href : input.url),
    ),
  );
  const view: ObservedWindow = {
    origin: ORIGIN,
    location: { href: `${ORIGIN}/me` },
    fetch: pageFetch,
    postMessage: (message, origin) => {
      posted.push({ message: message as ObservedMessage, origin });
    },
    addEventListener: (_type, listener) => {
      listeners.push(listener);
    },
  };
  installObserver(view);
  const message = (data: unknown, from: { source?: unknown; origin?: string } = {}) => {
    for (const listener of listeners) {
      listener({
        data,
        source: 'source' in from ? from.source : view,
        origin: from.origin ?? ORIGIN,
      } as MessageEvent);
    }
  };
  return { view, posted, pageFetch, message };
}

/** Lets the observer read the copy it took (it reads after handing the page its response). */
async function settled(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 0));
}

function fixtureAnswer(fixtures: Record<string, string>) {
  return (address: string) => {
    const path = new URL(address, ORIGIN).pathname;
    const name = fixtures[path];
    return name === undefined ? jsonResponse(404, {}) : jsonResponse(200, sunoFixture(name));
  };
}

describe('the page observer, replaying the TS-003 fixtures', () => {
  it('forwards a copy of each list response, with the paging fields of its request', async () => {
    const { view, posted } = suno(
      fixtureAnswer({
        '/api/feed/v3': 'feed-v3.library-page-1.response',
        '/api/clips/trashed_v2': 'clips-trashed-v2.response',
        '/api/project/me': 'project-me.page-1.response',
        '/api/playlist/me': 'playlist-me.response',
        '/api/unified/feed': 'unified-feed.playlist.response',
      }),
    );

    await view.fetch(`${API}/api/feed/v3`, {
      method: 'POST',
      body: JSON.stringify(sunoFixture('feed-v3.library-page-1.request')),
    });
    await view.fetch(`${API}/api/clips/trashed_v2?limit=20`);
    await view.fetch(
      `${API}/api/project/me?page=1&sort=max_created_at_last_&show_trashed=false&exclude_shared=false`,
    );
    await view.fetch(`${API}/api/playlist/me?page=1&show_trashed=false&show_sharelist=false`);
    await view.fetch(new URL(`${API}/api/unified/feed`), {
      method: 'POST',
      body: JSON.stringify(sunoFixture('unified-feed.playlist.request')),
    });
    await settled();

    expect(posted.map(({ message }) => message.kind)).toEqual([
      'library-feed',
      'trash',
      'workspaces',
      'playlists',
      'playlist-feed',
    ]);
    expect(posted.every(({ origin }) => origin === ORIGIN)).toBe(true);
    expect(new Set(posted.map(({ message }) => message.source))).toEqual(
      new Set([OBSERVER_SOURCE]),
    );
    expect(posted[0]?.message.body).toEqual(sunoFixture('feed-v3.library-page-1.response'));
    expect(posted[0]?.message.request).toEqual({
      cursor: '<redacted cursor>',
      page: null,
      filters: (sunoFixture('feed-v3.library-page-1.request') as { filters: unknown }).filters,
      feedId: null,
    });
    expect(posted[2]?.message.request.page).toBe(1);
    expect(posted[4]?.message.request.feedId).toBe(
      'generic_playlist:00000000-0000-4000-8000-00000000012b',
    );
  });

  it('forwards nothing of the Create request, which carries the token, nor of any other request', async () => {
    const { view, posted } = suno(
      fixtureAnswer({
        '/api/generate/v2-web/': 'generate-v2-web.songs-advanced.response',
        '/api/persona/get-personas/': 'persona-get-personas.response',
        '/api/download/authorize': 'download-authorize.response',
      }),
    );
    const create = sunoFixture('generate-v2-web.songs-simple.request');
    // The fixture keeps the fields, redacted, where the real request carries them.
    for (const secret of SECRETS) {
      expect(JSON.stringify(create)).toContain(`"${secret}"`);
    }

    const answer = await view.fetch(`${API}/api/generate/v2-web/`, {
      method: 'POST',
      body: JSON.stringify(create),
    });
    await view.fetch(`${API}/api/persona/get-personas/?page=1`);
    await view.fetch(`${API}/api/download/authorize`, { method: 'POST', body: '{}' });
    await settled();

    expect(posted).toEqual([]);
    // Complement: the page still got Suno's answer.
    expect(await answer.json()).toEqual(sunoFixture('generate-v2-web.songs-advanced.response'));
  });

  it('never forwards a token, session token, or tier, in a request or a response, at any depth', async () => {
    const response = sunoFixture('feed-v3.library-page-2.response') as {
      clips: Record<string, unknown>[];
    };
    response.clips[0] = { ...response.clips[0], token: 'T', metadata: { user_tier: 'pro' } };
    const withSecrets = { ...response, create_session_token: 'S' };
    const { view, posted } = suno(() => jsonResponse(200, withSecrets));

    await view.fetch(`${API}/api/feed/v3`, {
      method: 'POST',
      body: JSON.stringify({
        ...(sunoFixture('feed-v3.library-page-2.request') as object),
        token: 'T',
        create_session_token: 'S',
        user_tier: 'pro',
      }),
    });
    await settled();

    expect(posted).toHaveLength(1);
    const sent = JSON.stringify(posted);
    for (const secret of SECRETS) {
      expect(sent).not.toContain(`"${secret}"`);
    }
    expect(sent).not.toContain('"T"');
    // Complement: the rest of the clip is there.
    expect((posted[0]?.message.body as typeof response).clips[0]).toMatchObject({
      id: (sunoFixture('feed-v3.library-page-2.response') as typeof response).clips[0]?.id,
    });
  });

  it('reads no header, and sends the page request out exactly as the page made it', async () => {
    const { view, pageFetch, posted } = suno(() =>
      jsonResponse(200, sunoFixture('clips-trashed-v2.response')),
    );
    const read: PropertyKey[] = [];
    const headers = new Proxy(
      {},
      {
        get: (_target, key) => {
          read.push(key);
          return undefined;
        },
        ownKeys: () => {
          read.push('ownKeys');
          return [];
        },
      },
    );
    const init = {
      method: 'GET',
      headers: headers as HeadersInit,
      credentials: 'include' as const,
    };
    const request = new Request(`${API}/api/clips/trashed_v2?limit=20`);
    const watched = new Proxy(request, {
      get: (target, key) => {
        read.push(key === 'headers' ? 'request headers' : 'other');
        const value: unknown = Reflect.get(target, key, target);
        return typeof value === 'function' ? (value as () => unknown).bind(target) : value;
      },
    });

    await view.fetch(`${API}/api/clips/trashed_v2?limit=20`, init);
    await view.fetch(watched);
    await settled();

    expect(pageFetch.mock.calls[0]).toEqual([`${API}/api/clips/trashed_v2?limit=20`, init]);
    expect(pageFetch.mock.calls[0]?.[1]).toBe(init);
    expect(pageFetch.mock.calls[1]?.[0]).toBe(watched);
    expect(read).not.toContain('request headers');
    expect(read.filter((key) => key !== 'other')).toEqual([]);
    expect(posted).toHaveLength(2);
  });

  it('forwards nothing for a failed or non-JSON answer, and leaves the page its error', async () => {
    let next: Response | Error = jsonResponse(500, { detail: 'down' });
    const { view, posted } = suno(() => {
      if (next instanceof Error) {
        throw next;
      }
      return next;
    });

    expect((await view.fetch(`${API}/api/clips/trashed_v2`)).status).toBe(500);
    next = new Response('<html></html>', { status: 200 });
    expect(await (await view.fetch(`${API}/api/clips/trashed_v2`)).text()).toBe('<html></html>');
    next = new Error('offline');
    await expect(view.fetch(`${API}/api/clips/trashed_v2`)).rejects.toThrow('offline');
    await settled();

    expect(posted).toEqual([]);
  });

  it('sends what it saw again when the content script, from this page, says it is ready', async () => {
    const { view, posted, message } = suno(() =>
      jsonResponse(200, sunoFixture('clips-trashed-v2.response')),
    );
    await view.fetch(`${API}/api/clips/trashed_v2`);
    await settled();
    expect(posted).toHaveLength(1);

    // Another window or origin is ignored: the origin is checked on this side too.
    message({ source: CONTENT_SOURCE, type: 'observer-ready' }, { origin: 'https://evil.example' });
    message({ source: CONTENT_SOURCE, type: 'observer-ready' }, { source: null });
    message({ source: 'other', type: 'observer-ready' });
    expect(posted).toHaveLength(1);

    message({ source: CONTENT_SOURCE, type: 'observer-ready' });
    expect(posted).toHaveLength(2);
    expect(posted[1]).toEqual(posted[0]);
  });

  it('wraps the page fetch once, however often it is installed', async () => {
    const { view, posted, pageFetch } = suno(() =>
      jsonResponse(200, sunoFixture('clips-trashed-v2.response')),
    );
    installObserver(view);

    await view.fetch(`${API}/api/clips/trashed_v2`);
    await settled();

    expect(pageFetch).toHaveBeenCalledTimes(1);
    expect(posted).toHaveLength(1);
  });
});
