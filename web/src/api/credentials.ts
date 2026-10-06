import { useCallback, useEffect, useState } from 'react';
import { apiFetch } from './client';

const CREDENTIALS_PATH = 'api/v1/credentials';

export const CREDENTIAL_KINDS = ['api', 'extension', 'mcp-gateway'] as const;
export type CredentialKind = (typeof CREDENTIAL_KINDS)[number];

/** Every scope, in the order the API lists them. */
export const CREDENTIAL_SCOPES = [
  'catalog.read',
  'songs.write',
  'versions.write',
  'collections.write',
  'generations.evaluate',
  'artwork.write',
  'catalog.bulk-write',
  'suno.sync',
  'suno.generate',
] as const;

/** A credential as the API lists it: never its token. Times are UTC ISO 8601. */
export interface Credential {
  id: string;
  name: string;
  kind: string;
  scopes: string[];
  createdUtc: string;
  lastUsedUtc: string | null;
  revokedUtc: string | null;
  revision: number;
  /** What the last extension handshake with this credential's token reported; null until one is made. */
  lastExtensionVersion?: string | null;
  lastAdapterVersion?: string | null;
  lastSeenAt?: string | null;
}

export interface NewCredential {
  name: string;
  kind: CredentialKind;
  scopes: string[];
}

/** How a write ended, by the API's answer. Never a rejection. */
export type CredentialWriteResult =
  | { kind: 'done'; credential: Credential }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'conflict'; current: Credential; revoked: boolean }
  | { kind: 'not-found' }
  | { kind: 'failed' };

export type CreateCredentialResult =
  | { kind: 'created'; credential: Credential; token: string }
  | Exclude<CredentialWriteResult, { kind: 'done' }>;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

export function isCredential(value: unknown): value is Credential {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.name === 'string' &&
    typeof value.kind === 'string' &&
    Array.isArray(value.scopes) &&
    value.scopes.every((scope) => typeof scope === 'string') &&
    typeof value.createdUtc === 'string' &&
    isNullableString(value.lastUsedUtc) &&
    isNullableString(value.revokedUtc) &&
    typeof value.revision === 'number' &&
    (value.lastExtensionVersion === undefined || isNullableString(value.lastExtensionVersion)) &&
    (value.lastAdapterVersion === undefined || isNullableString(value.lastAdapterVersion)) &&
    (value.lastSeenAt === undefined || isNullableString(value.lastSeenAt))
  );
}

function isErrorMap(value: unknown): value is Record<string, string[]> {
  return (
    isRecord(value) &&
    Object.values(value).every(
      (messages) => Array.isArray(messages) && messages.every((m) => typeof m === 'string'),
    )
  );
}

async function body(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    return undefined;
  }
}

/** Only the listed fields, so the token is not kept anywhere the list is. */
function withoutToken(credential: Credential): Credential {
  const {
    id,
    name,
    kind,
    scopes,
    createdUtc,
    lastUsedUtc,
    revokedUtc,
    revision,
    lastExtensionVersion,
    lastAdapterVersion,
    lastSeenAt,
  } = credential;
  return {
    id,
    name,
    kind,
    scopes,
    createdUtc,
    lastUsedUtc,
    revokedUtc,
    revision,
    lastExtensionVersion,
    lastAdapterVersion,
    lastSeenAt,
  };
}

/** The refusals every write shares: field errors, a conflict with the current record, not found. */
function refusal(
  response: Response,
  problem: unknown,
): Exclude<CredentialWriteResult, { kind: 'done' }> {
  if (!isRecord(problem)) {
    return { kind: 'failed' };
  }
  if (
    response.status === 422 &&
    problem.code === 'validation_failed' &&
    isErrorMap(problem.errors)
  ) {
    return { kind: 'invalid', errors: problem.errors };
  }
  if (response.status === 409 && isCredential(problem.current)) {
    return {
      kind: 'conflict',
      current: problem.current,
      revoked: problem.code === 'credential_revoked',
    };
  }
  if (response.status === 404) {
    return { kind: 'not-found' };
  }
  return { kind: 'failed' };
}

/** Creates a credential. The token in the answer is the only copy there will ever be. */
export async function createCredential(request: NewCredential): Promise<CreateCredentialResult> {
  try {
    const response = await apiFetch(CREDENTIALS_PATH, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });
    const answer = await body(response);
    if (response.status === 201 && isCredential(answer)) {
      const token = 'token' in answer ? answer.token : undefined;
      return typeof token === 'string'
        ? { kind: 'created', credential: withoutToken(answer), token }
        : { kind: 'failed' };
    }
    return refusal(response, answer);
  } catch {
    return { kind: 'failed' };
  }
}

/** Renames a credential in force, given the revision it was loaded at. */
export async function renameCredential(
  credential: Pick<Credential, 'id' | 'revision'>,
  name: string,
): Promise<CredentialWriteResult> {
  try {
    const response = await apiFetch(`${CREDENTIALS_PATH}/${encodeURIComponent(credential.id)}`, {
      method: 'PATCH',
      headers: {
        'Content-Type': 'application/json',
        'If-Match': `"${String(credential.revision)}"`,
      },
      body: JSON.stringify({ name }),
    });
    const answer = await body(response);
    if (response.ok && isCredential(answer)) {
      return { kind: 'done', credential: answer };
    }
    return refusal(response, answer);
  } catch {
    return { kind: 'failed' };
  }
}

/** Revokes a credential; its token is refused from the next request on. Cannot be undone. */
export async function revokeCredential(id: string): Promise<CredentialWriteResult> {
  try {
    const response = await apiFetch(`${CREDENTIALS_PATH}/${encodeURIComponent(id)}/revoke`, {
      method: 'POST',
    });
    const answer = await body(response);
    if (response.ok && isCredential(answer)) {
      return { kind: 'done', credential: answer };
    }
    return refusal(response, answer);
  } catch {
    return { kind: 'failed' };
  }
}

export type CredentialsState =
  { phase: 'loading' } | { phase: 'error' } | { phase: 'ready'; credentials: Credential[] };

/** Loads every credential, revoked ones included. `reload` fetches the list again. */
export function useCredentials(): { state: CredentialsState; reload: () => void } {
  const [state, setState] = useState<CredentialsState>({ phase: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const load = async () => {
      try {
        const response = await apiFetch(CREDENTIALS_PATH, { signal: controller.signal });
        const answer = await body(response);
        if (controller.signal.aborted) {
          return;
        }
        if (response.ok && Array.isArray(answer) && answer.every(isCredential)) {
          setState({ phase: 'ready', credentials: answer });
        } else {
          setState((previous) => (previous.phase === 'ready' ? previous : { phase: 'error' }));
        }
      } catch {
        if (!controller.signal.aborted) {
          setState((previous) => (previous.phase === 'ready' ? previous : { phase: 'error' }));
        }
      }
    };
    void load();
    return () => {
      controller.abort();
    };
  }, [attempt]);

  const reload = useCallback(() => {
    setAttempt((previous) => previous + 1);
  }, []);

  return { state, reload };
}
