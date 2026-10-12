import { apiFetch } from './client';
import type { OptionValue } from './createFields';
import { failureOf, writeWithRevision, type FailureReason, type SaveResult } from './saves';
import { isRecord } from './songs';
import { isVersionDetail, type Version, type VersionDetail } from './versions';

// A Version's settings from the source chosen for its Cover, Extend, or Remix (#431). The API works
// out what the source fills and what differs (the rule is the application service's); the page only
// asks about the differing settings and sends back the ones the user takes.

const VERSIONS_PATH = 'api/v1/versions';

/** A setting's value as a Version read shows it: text, a number, a switch, a choice, none, or the Voice. */
export type SettingValue = OptionValue | { personaId: string; name: string };

/** Why a setting of the source is not known, so it is left as it is. */
export type UnknownReason = 'not_imported' | 'song_only' | 'deleted' | 'not_returned' | 'not_valid';

/** What choosing a source would do to a Version's settings. `action` is null for any other action. */
export interface SourceSettingsPlan {
  action: 'cover' | 'extend' | 'remix' | null;
  revision: number;
  fills: { key: string; sourceValue: SettingValue }[];
  conflicts: { key: string; versionValue: SettingValue; sourceValue: SettingValue }[];
  unknown: { key: string; reason: UnknownReason }[];
}

const REASONS: readonly string[] = [
  'not_imported',
  'song_only',
  'deleted',
  'not_returned',
  'not_valid',
];

function isPlan(value: unknown): value is SourceSettingsPlan {
  return (
    isRecord(value) &&
    (value.action === null ||
      value.action === 'cover' ||
      value.action === 'extend' ||
      value.action === 'remix') &&
    typeof value.revision === 'number' &&
    Array.isArray(value.fills) &&
    value.fills.every((fill) => isRecord(fill) && typeof fill.key === 'string') &&
    Array.isArray(value.conflicts) &&
    value.conflicts.every((conflict) => isRecord(conflict) && typeof conflict.key === 'string') &&
    Array.isArray(value.unknown) &&
    value.unknown.every(
      (item) =>
        isRecord(item) &&
        typeof item.key === 'string' &&
        typeof item.reason === 'string' &&
        REASONS.includes(item.reason),
    )
  );
}

/** What a preview came to. Never a rejection. */
export type PreviewResult =
  | { kind: 'planned'; plan: SourceSettingsPlan }
  | { kind: 'invalid'; errors: Record<string, string[]> }
  | { kind: 'failed'; reason: FailureReason };

/**
 * What setting `sources` (the Version's whole audio sources, as an edit's `inputs.sources`) would do
 * to the Version's settings. Changes nothing.
 */
export async function previewSourceSettings(
  versionId: string,
  sources: unknown,
): Promise<PreviewResult> {
  try {
    const response = await apiFetch(
      `${VERSIONS_PATH}/${encodeURIComponent(versionId)}/source-settings/preview`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sources }),
      },
    );
    let answer: unknown;
    try {
      answer = await response.json();
    } catch {
      answer = undefined;
    }
    if (response.ok && isPlan(answer)) {
      return { kind: 'planned', plan: answer };
    }
    if (
      response.status === 422 &&
      isRecord(answer) &&
      isRecord(answer.errors) &&
      Object.values(answer.errors).every((messages) => Array.isArray(messages))
    ) {
      return { kind: 'invalid', errors: answer.errors as Record<string, string[]> };
    }
    return { kind: 'failed', reason: response.ok ? 'server' : failureOf(response.status, answer) };
  } catch {
    return { kind: 'failed', reason: 'unreachable' };
  }
}

/**
 * Sets `sources` on the Version with every empty setting filled from the source and each of `take`
 * taken from it, in one edit, on the revision the plan was worked out at.
 */
export function applySourceSettings(
  version: Pick<Version, 'id'> & { revision: number },
  sources: unknown,
  take: readonly string[],
): Promise<SaveResult<VersionDetail>> {
  return writeWithRevision(
    'POST',
    `${VERSIONS_PATH}/${encodeURIComponent(version.id)}/source-settings`,
    version.revision,
    { sources, take: [...take] },
    (answer) => (isVersionDetail(answer) ? answer : undefined),
  );
}
