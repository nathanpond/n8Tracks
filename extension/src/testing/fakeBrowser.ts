import { vi } from 'vitest';
import type { BrowserApis } from '../background/connection.ts';

/**
 * A stand-in for the parts of `chrome.*` the service worker uses: storage that keeps what is set,
 * host permissions as a set of granted origins, and registered content scripts by ID. Every method
 * is a spy, so tests can check what was asked for.
 */
export function fakeBrowser(granted: string[] = []) {
  const stored = new Map<string, unknown>();
  const origins = new Set(granted);
  const scripts = new Map<string, chrome.scripting.RegisteredContentScript>();

  const browser = {
    storage: {
      get: vi.fn((keys: string[]) =>
        Promise.resolve(
          Object.fromEntries(
            keys.filter((key) => stored.has(key)).map((key) => [key, stored.get(key)]),
          ),
        ),
      ),
      set: vi.fn((items: Record<string, unknown>) => {
        for (const [key, value] of Object.entries(items)) {
          stored.set(key, structuredClone(value));
        }
        return Promise.resolve();
      }),
      remove: vi.fn((keys: string[]) => {
        for (const key of keys) {
          stored.delete(key);
        }
        return Promise.resolve();
      }),
    },
    permissions: {
      contains: vi.fn((request: { origins: string[] }) =>
        Promise.resolve(request.origins.every((origin) => origins.has(origin))),
      ),
      remove: vi.fn((request: { origins: string[] }) => {
        for (const origin of request.origins) {
          origins.delete(origin);
        }
        return Promise.resolve(true);
      }),
    },
    scripting: {
      registerContentScripts: vi.fn((list: chrome.scripting.RegisteredContentScript[]) => {
        for (const script of list) {
          if (scripts.has(script.id)) {
            return Promise.reject(new Error(`Duplicate script ID '${script.id}'`));
          }
          scripts.set(script.id, script);
        }
        return Promise.resolve();
      }),
      unregisterContentScripts: vi.fn((filter: { ids: string[] }) => {
        for (const id of filter.ids) {
          scripts.delete(id);
        }
        return Promise.resolve();
      }),
      getRegisteredContentScripts: vi.fn((filter: { ids: string[] }) =>
        Promise.resolve([...scripts.values()].filter((script) => filter.ids.includes(script.id))),
      ),
    },
  } satisfies BrowserApis;

  return {
    browser,
    stored,
    origins,
    scripts,
    /** What the user's permission prompt gives: adds the origins, as granting does. */
    grant: (more: string[]) => {
      for (const origin of more) {
        origins.add(origin);
      }
    },
  };
}

/** A JSON answer, fresh for each call. */
export function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}
