import { isRecord } from './songs';

/**
 * The local audio files a deletion leaves unassociated (#213), as each deletion impact counts them:
 * every one (Missing ones included), those associated by hand that no scan would associate again
 * after a restore, and those associated with the Song itself (a Song deletion only).
 */
export interface LocalAudioFiles {
  total: number;
  handAssociated: number;
  songLevel: number;
}

/** No files: what an impact without the count means. */
export const NO_LOCAL_AUDIO_FILES: LocalAudioFiles = { total: 0, handAssociated: 0, songLevel: 0 };

/**
 * The `localAudioFiles` of an impact answer: none when it is absent, undefined when it is there but
 * not three whole numbers.
 */
export function localAudioFilesOf(value: unknown): LocalAudioFiles | undefined {
  if (value === undefined) {
    return NO_LOCAL_AUDIO_FILES;
  }
  if (!isRecord(value)) {
    return undefined;
  }
  const { total, handAssociated, songLevel } = value;
  return wholeNumber(total) && wholeNumber(handAssociated) && wholeNumber(songLevel)
    ? { total, handAssociated, songLevel }
    : undefined;
}

function wholeNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0;
}
