import { apiFetch } from './client';
import { writeWithRevision, type SaveResult } from './saves';
import { body, isRecord, isSongArtist, useResource, type SongArtist } from './songs';

const CATALOG_SETTINGS_PATH = 'api/v1/settings/catalog';

/** The catalog settings: the default Artist new Songs are credited to, or null when there is none. */
export interface CatalogSettings {
  revision: number;
  defaultArtist: SongArtist | null;
}

export function isCatalogSettings(value: unknown): value is CatalogSettings {
  return (
    isRecord(value) &&
    typeof value.revision === 'number' &&
    (value.defaultArtist === null || isSongArtist(value.defaultArtist))
  );
}

const acceptSettings = (answer: unknown) => (isCatalogSettings(answer) ? answer : undefined);

/** The catalog settings, for Settings → Catalog. */
export function useCatalogSettings() {
  return useResource(CATALOG_SETTINGS_PATH, acceptSettings);
}

/** The catalog settings as they are now; undefined when they cannot be read. */
export async function readCatalogSettings(
  signal?: AbortSignal,
): Promise<CatalogSettings | undefined> {
  try {
    const response = await apiFetch(CATALOG_SETTINGS_PATH, { signal });
    const answer = await body(response);
    return response.ok && isCatalogSettings(answer) ? answer : undefined;
  } catch {
    return undefined;
  }
}

/**
 * Sets the default Artist (its ID, or null to clear it), based on the settings' revision; a stale
 * revision comes back as a conflict with the settings as they are now.
 */
export function setDefaultArtist(
  settings: Pick<CatalogSettings, 'revision'>,
  artistId: string | null,
): Promise<SaveResult<CatalogSettings>> {
  return writeWithRevision(
    'PUT',
    CATALOG_SETTINGS_PATH,
    settings.revision,
    { defaultArtistId: artistId },
    acceptSettings,
  );
}
