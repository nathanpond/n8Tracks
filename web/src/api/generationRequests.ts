import { useCallback, useEffect, useState } from 'react';
import { apiFetch } from './client';
import { body, isRecord, type LoadState } from './songs';

/** How often the Version page reads an active request again. */
export const REQUEST_POLL_MS = 2_000;

/** A request's states: the first six are active, the last four end it. */
export const ACTIVE_STATES = ['pending', 'claimed', 'opening', 'workspace', 'filling', 'waiting'];
export const ENDED_STATES = ['done', 'stopped', 'cancelled', 'expired'];

/** A value of the verification summary: text only as its length and hash (#146). */
export type VerificationValue =
  string | number | boolean | null | { length: number; sha256: string };

/** What became of one entry of Suno's Create form when the extension filled it (#146). */
export type VerificationOutcome =
  'set' | 'failed' | 'unavailable' | 'manual' | 'not_applicable' | 'unsupported';

export interface VerificationEntry {
  key: string;
  outcome: VerificationOutcome;
  expected?: VerificationValue;
  found?: VerificationValue;
  note?: string;
}

/** The extension's last verification summary of the filled form (#146). */
export interface Verification {
  adapterVersion: number;
  mode: string;
  checkedAt: string;
  entries: VerificationEntry[];
}

/** What one of the user's Creates in Suno came to (#149). */
export interface ObservedCreate {
  observedAt: string;
  /** `attached` to the requested Version, `branched` to a new Version, or `none` (every clip skipped). */
  outcome: 'attached' | 'branched' | 'none';
  version: { id: string; number: string; shortcode: string } | null;
  /** The options (API names; `sources` for the lineage) in which what was submitted differed. */
  differing: string[];
  /** The options neither Suno's answer nor the page's request gave, taken from the Version. */
  assumed: string[];
  /** Whether the values the page sent were read. */
  requestRead: boolean;
  generations: { id: string; shortcode: string; sunoId: string }[];
  skipped: { sunoId: string; reason: string }[];
}

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
  /** Absent in answers from before #146, null until the extension reports one. */
  verification?: Verification | null;
  /** What each of the user's Creates came to, oldest first (#149); absent in answers from before it. */
  observed?: ObservedCreate[];
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
    nullableText(value.endedAt) &&
    (value.verification === undefined ||
      value.verification === null ||
      isVerification(value.verification)) &&
    (value.observed === undefined ||
      (Array.isArray(value.observed) && value.observed.every(isObservedCreate)))
  );
}

function isTextList(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string');
}

function isObservedCreate(value: unknown): value is ObservedCreate {
  return (
    isRecord(value) &&
    typeof value.observedAt === 'string' &&
    (value.outcome === 'attached' || value.outcome === 'branched' || value.outcome === 'none') &&
    (value.version === null ||
      (isRecord(value.version) &&
        typeof value.version.id === 'string' &&
        typeof value.version.number === 'string' &&
        typeof value.version.shortcode === 'string')) &&
    isTextList(value.differing) &&
    isTextList(value.assumed) &&
    typeof value.requestRead === 'boolean' &&
    Array.isArray(value.generations) &&
    value.generations.every(
      (item) =>
        isRecord(item) &&
        typeof item.id === 'string' &&
        typeof item.shortcode === 'string' &&
        typeof item.sunoId === 'string',
    ) &&
    Array.isArray(value.skipped) &&
    value.skipped.every(
      (item) =>
        isRecord(item) && typeof item.sunoId === 'string' && typeof item.reason === 'string',
    )
  );
}

const OUTCOMES: readonly string[] = [
  'set',
  'failed',
  'unavailable',
  'manual',
  'not_applicable',
  'unsupported',
] satisfies VerificationOutcome[];

function isVerification(value: unknown): value is Verification {
  return (
    isRecord(value) &&
    typeof value.adapterVersion === 'number' &&
    typeof value.mode === 'string' &&
    typeof value.checkedAt === 'string' &&
    Array.isArray(value.entries) &&
    value.entries.every(
      (entry) =>
        isRecord(entry) &&
        typeof entry.key === 'string' &&
        typeof entry.outcome === 'string' &&
        OUTCOMES.includes(entry.outcome),
    )
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
