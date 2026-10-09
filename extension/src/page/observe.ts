import {
  downloadRequestOf,
  forwardedBody,
  isObserverReady,
  observedKindOf,
  observedRequestOf,
  OBSERVER_BUFFER,
  OBSERVER_SOURCE,
  submittedOf,
  type ObservedMessage,
} from '../adapter/observed.ts';

/**
 * The page observer: runs in Suno's own page (the MAIN world, from `document_start`) and wraps the
 * page's `fetch`. The page's request goes out exactly as the page made it; when its response is
 * one of the lists the adapter reads (`adapter/observed.ts`), a copy of the body is posted to the
 * content script, which checks the origin as this does. Nothing else is read: no header, no
 * cookie, and no request body beyond the paging fields and, for the user's own Create click (#149),
 * the values at the import field map's `createRequest` paths; of the plan's billing answer only the
 * download counts go (#215), and of a prepared download only its status and address (#216);
 * `token`, `create_session_token`, and
 * `user_tier` are left out at any depth (invariant 6). It sends no request of its own (invariant
 * 4's guard allows this file to name `fetch` only to wrap it).
 *
 * The content script starts later than this (`document_idle`), so the last responses are kept and
 * sent again when it says it is ready.
 */

/** The part of `window` the observer uses, so tests can stand in for the page. */
export interface ObservedWindow {
  fetch: typeof fetch;
  origin: string;
  location: { href: string };
  postMessage(message: unknown, targetOrigin: string): void;
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void;
}

const installed = Symbol.for('n8tracks.page-observer');

/** The address and method of a `fetch` call, read the way `fetch` itself would read them. */
function requestOf(input: Parameters<typeof fetch>[0], init: RequestInit | undefined) {
  if (typeof input === 'string') {
    return { address: input, method: init?.method ?? 'GET' };
  }
  if ('href' in input) {
    return { address: input.href, method: init?.method ?? 'GET' };
  }
  return { address: input.url, method: init?.method ?? input.method };
}

/**
 * The body of a `fetch` call the observer reads, as `fetch` itself would take it: `init.body` when
 * the call gives one, else the body of a `Request` passed as the first argument (TS-007: Suno's page
 * now calls `fetch(request, { headers })`, the method and JSON body on the `Request`). A `Request`'s
 * body can be read only once, so a clone is read, taken before the page's request goes out; a body
 * already used, or one that cannot be read, is read as none. Never rejects.
 */
function bodyOf(
  input: Parameters<typeof fetch>[0],
  init: RequestInit | undefined,
): Promise<unknown> {
  if (init?.body !== undefined && init.body !== null) {
    return Promise.resolve(init.body);
  }
  if (typeof input === 'string' || 'href' in input || input.bodyUsed) {
    return Promise.resolve(undefined);
  }
  try {
    return input
      .clone()
      .text()
      .catch(() => undefined);
  } catch {
    return Promise.resolve(undefined);
  }
}

/** Wraps `view.fetch`; a second call on the same window does nothing. */
export function installObserver(view: ObservedWindow): void {
  const marked = view as ObservedWindow & { [installed]?: true };
  if (marked[installed] === true) {
    return;
  }
  marked[installed] = true;

  const original = view.fetch.bind(view);
  const kept: ObservedMessage[] = [];

  const forward = (message: ObservedMessage) => {
    kept.push(message);
    if (kept.length > OBSERVER_BUFFER) {
      kept.shift();
    }
    view.postMessage(message, view.origin);
  };

  const observe = async (
    response: Response,
    kind: NonNullable<ReturnType<typeof observedKindOf>>,
    address: string,
    sent: Promise<unknown>,
  ) => {
    if (!response.ok) {
      return;
    }
    try {
      const copy: unknown = await response.clone().json();
      const body = await sent;
      forward({
        source: OBSERVER_SOURCE,
        type: 'observed',
        kind,
        request: observedRequestOf(address, view.location.href, body),
        body: forwardedBody(kind, copy),
        ...(kind === 'create' ? { submitted: submittedOf(body) } : {}),
        ...(kind === 'download-clip'
          ? { download: downloadRequestOf(address, view.location.href) }
          : {}),
      });
    } catch {
      // Not JSON: the reader sees nothing and stops at its step, with the step named.
    }
  };

  view.fetch = async (...args: Parameters<typeof fetch>) => {
    const [input, init] = args;
    let seen: { kind: NonNullable<ReturnType<typeof observedKindOf>>; address: string } | null =
      null;
    let sent: Promise<unknown> = Promise.resolve(undefined);
    try {
      const { address, method } = requestOf(input, init);
      const kind = observedKindOf(address, method, view.location.href);
      if (kind !== null) {
        seen = { kind, address };
        // Taken before the request goes out: once the page's fetch has read a Request's body, it
        // cannot be cloned.
        sent = bodyOf(input, init);
      }
    } catch {
      // Observing never changes what the page gets.
    }
    const response = await original(...args);
    if (seen !== null) {
      // Never rejects: `observe` catches what it reads, and `sent` never rejects.
      void observe(response, seen.kind, seen.address, sent);
    }
    return response;
  };

  view.addEventListener('message', (event) => {
    if (event.source !== (view as unknown) || event.origin !== view.origin) {
      return;
    }
    if (isObserverReady(event.data)) {
      for (const message of kept) {
        view.postMessage(message, view.origin);
      }
    }
  });
}
