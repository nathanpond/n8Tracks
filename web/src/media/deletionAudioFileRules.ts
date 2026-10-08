import type { LocalAudioFiles } from '../api/localAudioFiles';

/** What a deletion is of, as its confirmation names the files' owner. */
export type DeletedOwner = 'generation' | 'version' | 'song';

function files(n: number): string {
  return `${String(n)} local audio ${n === 1 ? 'file' : 'files'}`;
}

/**
 * What a deletion confirmation says about local audio files (#213), one sentence per line: how many
 * are associated (Missing ones included), that they stay on disk and will appear in Unmatched Files,
 * which Song-level associations cannot be restored, and which hand-made ones a restore will not make
 * again. No lines when there are no files.
 */
export function deletionAudioFileLines(counts: LocalAudioFiles, owner: DeletedOwner): string[] {
  const { total, handAssociated, songLevel } = counts;
  if (total === 0) {
    return [];
  }
  const one = total === 1;
  const lines = [
    owner === 'version'
      ? `Its Generations have ${files(total)}. ${one ? 'It stays' : 'They stay'} on disk and will appear in Unmatched Files.`
      : `${files(total)} ${one ? 'is' : 'are'} associated with it. ${one ? 'It stays' : 'They stay'} on disk and will appear in Unmatched Files.`,
  ];
  if (songLevel > 0) {
    lines.push(
      `${songLevel === total ? (one ? 'It is' : 'They are') : `${String(songLevel)} of them ${songLevel === 1 ? 'is' : 'are'}`} associated with the Song itself; a restore cannot bring ${songLevel === 1 ? 'that association' : 'those associations'} back.`,
    );
  }
  if (handAssociated > 0) {
    lines.push(
      `${handAssociated === total ? (one ? 'It was' : 'They were') : `${String(handAssociated)} of them ${handAssociated === 1 ? 'was' : 'were'}`} associated by hand without a Suno ID in ${handAssociated === 1 ? 'its name' : 'their names'}, so ${handAssociated === 1 ? 'it' : 'they'} will not be associated again automatically after a restore.`,
    );
  }
  return lines;
}

/**
 * What the "Create new Song from Generation" warning says about its local audio files (#213), or
 * undefined when it has none: they move with it (Missing ones included), with its Preferred Audio
 * File choice, and the Song-level files stay with `songShortcode`.
 */
export function movedAudioFilesLine(count: number, songShortcode: string): string | undefined {
  return count === 0
    ? undefined
    : `Its ${files(count)} and its Preferred Audio File choice move with it; the files stay where they are on disk. Files associated with ${songShortcode} itself stay with ${songShortcode}.`;
}
