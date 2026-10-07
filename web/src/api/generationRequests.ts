import { useCallback, useEffect, useState } from 'react';
import { apiFetch } from './client';
import { body, isRecord, type LoadState } from './songs';

/** How often the Version page reads an active request again. */
export const REQUEST_POLL_MS = 2_000;

/** A request's states: the first six are active, the last four end it. */
export const ACTIVE_STATES = ['pending', 'claimed', 'opening', 'workspace', 'filling', 'waiting'];
export const ENDED_STATES = ['done', 'stopped', 'cancelled', 'expired'];

/** A Generate on Suno request as the Version page sees it (never its snapshot). */
export interface GenerationRequest {
  id: string;
  versionId: string;
  state: string;
  active: boolean;
  step: string | null;
  message: string | null;
  claimed: boolean;
  createdAt: string;
  updatedAt: string;
  endedAt: string | null;
}

/** A source that blocks a request, as n8Tracks names it. */
export interface UnavailableSource {
  group: 'audio' | 'inspiration';
  position: number;
  title: string | null;
  shortcode: string | null;
  availability: 'deleted' | 'trashed' | 'missing';
}

export type CreateRequestResult =
  | { kind: 'created'; request: GenerationRequest }
  | { kind: 'blocked'; sources: UnavailableSource[]; lastSyncAt: string | null }
  | { kind: 'gone' }
  | { kind: 'failed' };

export type CancelRequestResult =
  { kind: 'cancelled'; request: GenerationRequest } | { kind: 'ended' } | { kind: 'failed' };

function nullableText(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

export function isGenerationRequest(value: unknown): value is GenerationRequest {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.versionId === 'string' &&
    typeof value.state === 'string' &&
    typeof value.active === 'boolean' &&
    nullableText(value.step) &&
    nullableText(value.message) &&
    typeof value.claimed === 'boolean' &&
    typeof value.createdAt === 'string' &&
    typeof value.updatedAt === 'string' &&
    nullableText(value.endedAt)
  );
}

function isUnavailableSource(value: unknown): value is UnavailableSource {
  return (
    isRecord(value) &&
    (value.group === 'audio' || value.group === 'inspiration') &&
    typeof value.position === 'number' &&
    nullableText(value.title) &&
    nullableText(value.shortcode) &&
    (value.availability === 'deleted' ||
      value.availability === 'trashed' ||
      value.availability === 'missing')
  );
}

const versionPath = (versionId: string) => `api/v1/versions/${encodeURIComponent(versionId)}`;
const requestPath = (id: string) => `api/v1/suno/generation-requests/${encodeURIComponent(id)}`;

/** Makes a request from the Version, as it is now. */
export async function createGenerationRequest(versionId: string): Promise<CreateRequestResult> {
  try {
    const response = await apiFetch(`${versionPath(versionId)}/generation-requests`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: '{}',
    });
    const answer = await body(response);
    if (response.ok) {
      return isGenerationRequest(answer)
        ? { kind: 'created', request: answer }
        : { kind: 'failed' };
    }
    if (response.status === 404) {
      return { kind: 'gone' };
    }
    if (
      response.status === 422 &&
      isRecord(answer) &&
      answer.code === 'sources_unavailable' &&
      Array.isArray(answer.sources) &&
      answer.sources.every(isUnavailableSource) &&
      nullableText(answer.lastSyncAt ?? null)
    ) {
      return {
        kind: 'blocked',
        sources: answer.sources,
        lastSyncAt: (answer.lastSyncAt as string | null | undefined) ?? null,
      };
    }
    return { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/** Cancels an active request. */
export async function cancelGenerationRequest(id: string): Promise<CancelRequestResult> {
  try {
    const response = await apiFetch(`${requestPath(id)}/cancel`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: '{}',
    });
    const answer = await body(response);
    if (response.ok && isGenerationRequest(answer)) {
      return { kind: 'cancelled', request: answer };
    }
    return response.status === 409 ? { kind: 'ended' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * The Version's newest request (null when it has none), read again every {@link REQUEST_POLL_MS}
 * while it is active. A read again that fails keeps what is shown. `reload` reads it now; `show`
 * replaces it with one the page already has (a request just made or cancelled).
 */
export function useGenerationRequest(versionId: string): {
  state: LoadState<GenerationRequest | null>;
  reload: () => void;
  show: (request: GenerationRequest) => void;
} {
  const [loaded, setLoaded] = useState<{
    versionId: string;
    state: LoadState<GenerationRequest | null>;
  }>({ versionId, state: { phase: 'loading' } });
  const [attempt, setAttempt] = useState(0);
  const state: LoadState<GenerationRequest | null> =
    loaded.versionId === versionId ? loaded.state : { phase: 'loading' };

  useEffect(() => {
    const controller = new AbortController();
    const settle = (next: LoadState<GenerationRequest | null>) => {
      if (controller.signal.aborted) {
        return;
      }
      setLoaded((previous) =>
        next.phase !== 'ready' &&
        previous.versionId === versionId &&
        previous.state.phase === 'ready'
          ? previous
          : { versionId, state: next },
      );
    };
    const load = async () => {
      try {
        const response = await apiFetch(`${versionPath(versionId)}/generation-request`, {
          signal: controller.signal,
        });
        const answer = await body(response);
        if (
          response.ok &&
          isRecord(answer) &&
          (answer.request === null || isGenerationRequest(answer.request))
        ) {
          settle({ phase: 'ready', data: answer.request });
        } else {
          settle(response.status === 404 ? { phase: 'not-found' } : { phase: 'error' });
        }
      } catch {
        settle({ phase: 'error' });
      }
    };
    void load();
    return () => {
      controller.abort();
    };
  }, [versionId, attempt]);

  // While the request is active it is read again after a while; each answer starts the wait anew.
  const active = state.phase === 'ready' && state.data?.active === true;
  useEffect(() => {
    if (!active) {
      return undefined;
    }
    const timer = setTimeout(() => {
      setAttempt((previous) => previous + 1);
    }, REQUEST_POLL_MS);
    return () => {
      clearTimeout(timer);
    };
  }, [active, loaded]);

  const reload = useCallback(() => {
    setAttempt((previous) => previous + 1);
  }, []);
  const show = useCallback(
    (request: GenerationRequest) => {
      setLoaded({ versionId, state: { phase: 'ready', data: request } });
    },
    [versionId],
  );
  return { state, reload, show };
}
