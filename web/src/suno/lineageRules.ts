import type {
  ImportChoice,
  ImportLineage,
  ImportLineageRecord,
  ImportLineageSource,
} from '../api/sunoImports';
import { formatPosition } from '../versions/sourcesRules';

/**
 * The words of the import review's Lineage column (#153): what each record's clip was made from, and
 * where each source is. Pure, so the column's text is tested here.
 */

/** How an audio source's action reads before its titles, by the Suno action its type stands for. */
const AUDIO_VERBS: Readonly<Record<string, string>> = {
  cover: 'Cover of',
  extend: 'Extension of',
  mashup: 'Mashup of',
  sample: 'Sample of',
  reuse_prompt: 'Prompt reused from',
};

/** What a source's group and action read as: "Cover of", "Inspired by", or "<type> of" for another type (Remix). */
export function sourceVerb(source: ImportLineageSource): string {
  if (source.group === 'inspiration') {
    return 'Inspired by';
  }
  const verb = source.sunoAction === null ? undefined : AUDIO_VERBS[source.sunoAction];
  return verb ?? `${source.typeName === '' ? 'Made' : source.typeName} of`;
}

/** Whether the record of this export a source names is set to be imported. */
export function isImporting(record: ImportLineageRecord | null): boolean {
  return record?.choice?.action === 'import';
}

/**
 * Where a source is, in words, or null for a Generation (the column links to it instead): in this sync
 * (and whether it is chosen for import), or not in this sync.
 */
export function placeText(source: ImportLineageSource): string | null {
  switch (source.place) {
    case 'generation':
      return null;
    case 'export':
      return isImporting(source.record) ? 'in this sync' : 'in this sync, not chosen for import';
    default:
      return 'not in this sync';
  }
}

function titleOf(source: ImportLineageSource): string {
  const position =
    source.sunoAction === 'extend' && source.continueAtSeconds !== null
      ? ` at ${formatPosition(source.continueAtSeconds)}`
      : '';
  return `${source.title}${position}`;
}

/** One group's sources as a line: when every source is in one place, it is said once at the end. */
function groupLine(sources: ImportLineageSource[]): string {
  const first = sources[0];
  if (first === undefined) {
    return '';
  }
  const places = sources.map(placeText);
  const shared = places.every((place) => place === places[0]) ? places[0] : undefined;
  const titles = sources
    .map((source, index) => {
      const place = places[index] ?? null;
      return shared !== undefined || place === null
        ? titleOf(source)
        : `${titleOf(source)} (${place})`;
    })
    .join(' + ');
  return `${sourceVerb(first)} ${titles}${shared !== undefined && shared !== null ? `: ${shared}` : ''}`;
}

/**
 * The lines of a record's lineage: the audio sources ("Cover of A: not in this sync", "Mashup of A +
 * B"), the Inspiration songs or playlist, and the Voice. Empty for a record made from nothing.
 */
export function lineageLines(lineage: ImportLineage | null | undefined): string[] {
  if (lineage === null || lineage === undefined) {
    return [];
  }
  const lines: string[] = [];
  const audio = lineage.sources.filter((source) => source.group === 'audio');
  const inspiration = lineage.sources.filter((source) => source.group === 'inspiration');
  if (audio.length > 0) {
    lines.push(groupLine(audio));
  }
  if (inspiration.length > 0) {
    lines.push(groupLine(inspiration));
  }
  if (lineage.playlist !== null) {
    const { name, sunoPlaylistId, clipCount } = lineage.playlist;
    lines.push(
      `Inspired by playlist ${name === '' ? sunoPlaylistId : name} (${clipCount === 1 ? '1 clip' : `${String(clipCount)} clips`})`,
    );
  }
  if (lineage.voice !== null) {
    lines.push(
      `Voice: ${lineage.voice.name === '' ? lineage.voice.personaId : lineage.voice.name}`,
    );
  }
  return lines;
}

/** The sources a user may include in this import: records of this export not chosen for import, each once. */
export function includable(lineage: ImportLineage | null | undefined): ImportLineageSource[] {
  const seen = new Set<string>();
  return (lineage?.sources ?? []).filter((source) => {
    if (source.place !== 'export' || isImporting(source.record) || seen.has(source.sunoId)) {
      return false;
    }
    seen.add(source.sunoId);
    return true;
  });
}

/**
 * The choice that includes a source's record in the import: what n8Tracks proposed for it when that
 * was an import, otherwise a new Song with its title, in its workspace, under `nextKey`.
 */
export function includeChoice(record: ImportLineageRecord, nextKey: string): ImportChoice {
  const proposed = record.proposal?.choice;
  if (proposed?.action === 'import') {
    return proposed;
  }
  return {
    action: 'import',
    target: {
      kind: 'newSong',
      key: nextKey,
      title: record.title ?? 'Untitled',
      workspaceId: record.workspaceId,
    },
  };
}
