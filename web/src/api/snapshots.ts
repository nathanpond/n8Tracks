import { resolveAppUrl } from './baseUrl';
import { apiFetch } from './client';
import { writeWithRevision, type SaveResult } from './saves';
import { ANTIFORGERY_HEADER } from './session';
import { body, isRecord, useResource } from './songs';
import { isVersionDetail, type Version, type VersionDetail } from './versions';

const VERSIONS_PATH = 'api/v1/versions';

/** A snapshot of a Version's lyrics and styles, as the History list shows it. Times are UTC ISO 8601. */
export interface SnapshotSummary {
  id: string;
  versionId: string;
  createdAt: string;
}

/** A snapshot with its text: line endings as `\n`, otherwise as the editor had it. */
export interface Snapshot extends SnapshotSummary {
  lyrics: string;
  styles: string;
}

/** What a snapshot request sends: the text in the editor and when it was captured. */
export interface SnapshotText {
  lyrics: string;
  styles: string;
  capturedAt: string;
}

function isSnapshotSummary(value: unknown): value is SnapshotSummary {
  return (
    isRecord(value) &&
    typeof value.id === 'string' &&
    typeof value.versionId === 'string' &&
    typeof value.createdAt === 'string'
  );
}

function isSnapshot(value: unknown): value is Snapshot {
  return (
    isSnapshotSummary(value) &&
    isRecord(value) &&
    typeof value.lyrics === 'string' &&
    typeof value.styles === 'string'
  );
}

function snapshotsPath(versionId: string): string {
  return `${VERSIONS_PATH}/${encodeURIComponent(versionId)}/snapshots`;
}

const acceptSnapshots = (answer: unknown) =>
  isRecord(answer) && Array.isArray(answer.items) && answer.items.every(isSnapshotSummary)
    ? answer.items
    : undefined;

/** A Version's snapshots, newest first, without their text. */
export function useSnapshotList(versionId: string) {
  return useResource(snapshotsPath(versionId), acceptSnapshots);
}

/**
 * How sending a snapshot ended. `retryable` is false when the API refused it in a way sending it
 * again cannot fix (the Version is gone, the text is over a limit): it is dropped.
 */
export type SnapshotSendResult =
  { kind: 'stored'; snapshot: Snapshot } | { kind: 'failed'; retryable: boolean };

/**
 * Sends a snapshot of `versionId`'s text. 201 (stored) and 200 (the same as the newest, which is
 * kept instead) are both stored. No answer, a server error, a timeout, or throttling may go
 * through later; any other refusal will not.
 */
export async function postSnapshot(
  versionId: string,
  text: SnapshotText,
): Promise<SnapshotSendResult> {
  try {
    const response = await apiFetch(snapshotsPath(versionId), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(text),
    });
    const answer = await body(response);
    if (response.ok && isSnapshot(answer)) {
      return { kind: 'stored', snapshot: answer };
    }
    const status = response.status;
    return {
      kind: 'failed',
      retryable: response.ok || status >= 500 || status === 408 || status === 429,
    };
  } catch {
    return { kind: 'failed', retryable: true };
  }
}

/**
 * Sends a snapshot as the page closes: a `keepalive` POST the browser finishes after the page is
 * gone. Nothing reads its answer, as for the closing save (`sendVersionAsPageCloses`).
 */
export function sendSnapshotAsPageCloses(versionId: string, text: SnapshotText): void {
  try {
    fetch(resolveAppUrl(snapshotsPath(versionId)), {
      method: 'POST',
      keepalive: true,
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        [ANTIFORGERY_HEADER]: '1',
      },
      body: JSON.stringify(text),
    }).catch(() => undefined);
  } catch {
    // The page is going away; there is no one left to tell.
  }
}

/** One snapshot with its text, or why it could not be read. Never a rejection. */
export type SnapshotReadResult =
  { kind: 'found'; snapshot: Snapshot } | { kind: 'gone' } | { kind: 'failed' };

export async function fetchSnapshot(
  versionId: string,
  snapshotId: string,
  signal?: AbortSignal,
): Promise<SnapshotReadResult> {
  try {
    const response = await apiFetch(
      `${snapshotsPath(versionId)}/${encodeURIComponent(snapshotId)}`,
      { signal },
    );
    const answer = await body(response);
    if (response.ok && isSnapshot(answer)) {
      return { kind: 'found', snapshot: answer };
    }
    return response.status === 404 ? { kind: 'gone' } : { kind: 'failed' };
  } catch {
    return { kind: 'failed' };
  }
}

/**
 * How deleting a history entry ended. `gone` means the Version has no such entry (any more), so
 * it is not in the history either way. Never a rejection.
 */
export type SnapshotDeleteResult = 'deleted' | 'gone' | 'failed';

/** Deletes one entry of a Version's history. It needs no revision. */
export async function deleteSnapshot(
  versionId: string,
  snapshotId: string,
): Promise<SnapshotDeleteResult> {
  try {
    const response = await apiFetch(
      `${snapshotsPath(versionId)}/${encodeURIComponent(snapshotId)}`,
      { method: 'DELETE' },
    );
    if (response.ok) {
      return 'deleted';
    }
    return response.status === 404 ? 'gone' : 'failed';
  } catch {
    return 'failed';
  }
}

const acceptVersionDetail = (answer: unknown) => (isVersionDetail(answer) ? answer : undefined);

/**
 * Replaces a Version's lyrics and styles with a snapshot's, based on the revision the Version was
 * read at; the API snapshots the text it replaces first. A stale revision is a conflict carrying
 * the Version as it is now.
 */
export function restoreSnapshot(
  version: Pick<Version, 'id' | 'revision'>,
  snapshotId: string,
): Promise<SaveResult<VersionDetail>> {
  return writeWithRevision(
    'POST',
    `${snapshotsPath(version.id)}/${encodeURIComponent(snapshotId)}/restore`,
    version.revision,
    {},
    acceptVersionDetail,
  );
}
