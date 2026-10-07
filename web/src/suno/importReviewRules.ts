import type {
  ImportChoice,
  ImportFilter,
  ImportRecord,
  ImportState,
  NamedTarget,
  RecordClass,
} from '../api/sunoImports';
import { RECORD_CLASSES } from '../api/sunoImports';

/** A record's class in plain words. */
export const CLASS_LABELS: Record<RecordClass, string> = {
  new: 'New',
  linked: 'Already linked',
  changed: 'Changed',
  conflict: 'Conflict',
  ignored: 'Ignored',
  deleted: 'Deleted in n8Tracks',
};

/** The flag the import mapping sets on a clip whose kind it could not tell (#136). */
export const UNKNOWN_KIND_FLAG = 'unknown_kind';

/** Whether the review can change a record's choice: a record a Generation holds cannot be imported again. */
export function isReviewable(record: ImportRecord): boolean {
  return record.class === 'new' || record.class === 'ignored' || record.class === 'deleted';
}

/** What Suno's library filters left out, in plain words, each kind once (`stem` and `stemComplement` are both stems). */
export function excludedKindsText(kinds: readonly string[]): string[] {
  const words: Record<string, string> = {
    disliked: 'disliked songs',
    stem: 'stems',
    stemComplement: 'stems',
    fromStudioProject: 'clips made in Studio projects',
  };
  return [...new Set(kinds.map((kind) => words[kind] ?? kind))];
}

/** A refusal reason in plain words. */
export function reasonText(reason: string): string {
  switch (reason) {
    case 'inputs_differ':
      return 'its creation inputs differ from that Version’s, or from the other records going there';
    case 'target_missing':
      return 'the Song or Version chosen no longer exists';
    case 'parent_not_in_song':
      return 'the parent Version is not in that Song';
    case 'invalid_number':
      return 'that Version number is taken or not allowed';
    case 'invalid_title':
      return 'the new Song’s title is not valid';
    case 'target_conflict':
      return 'other records describe that new Song or Version differently';
    case 'already_linked':
      return 'a Generation already holds it';
    case 'record_not_found':
      return 'the record is no longer in this import';
    case 'not_changed':
      return 'only a Changed record takes Suno’s fields';
    case 'not_conflict':
      return 'only a Conflict record can move to a new Version or be kept';
    case 'field_not_changed':
      return 'Suno’s data does not differ in a field chosen';
    default:
      return reason;
  }
}

/** Why a record was not imported as chosen when the import was confirmed (#140), in plain words. */
export function commitReasonText(reason: string): string {
  switch (reason) {
    case 'tombstoned':
      return 'its Generation was deleted from n8Tracks after the review was opened';
    case 'invalid_clip':
      return 'n8Tracks could not keep this clip as a Generation';
    case 'number_taken':
      return 'its Version number was taken meanwhile, so the Version took the next one';
    case 'inputs_differ':
      return 'its creation inputs no longer match that Version’s';
    default:
      return reasonText(reason);
  }
}

/** What confirming created: "Created 1 Song, 2 Versions, and 3 Generations." */
export function createdText(created: {
  songs: number;
  versions: number;
  generations: number;
}): string {
  return `Created ${countText(created.songs, 'Song')}, ${countText(created.versions, 'Version')}, and ${countText(created.generations, 'Generation')}.`;
}

/** How many records ended each way, in plain words, leaving out the outcomes none had. */
export function outcomesText(records: readonly { outcome: string }[]): string {
  const labels: [string, string][] = [
    ['created', 'imported'],
    ['linked', 'already linked'],
    ['skipped', 'left for a later sync'],
    ['ignored', 'not copied'],
    ['updated', 'updated from Suno'],
    ['declined', 'kept as they were'],
    ['kept', 'kept on their Version'],
    ['moved', 'moved to a new Version'],
    ['failed', 'failed'],
  ];
  const parts = labels.flatMap(([outcome, label]) => {
    const count = records.filter((record) => record.outcome === outcome).length;
    return count === 0 ? [] : [`${recordCountText(count)} ${label}`];
  });
  return parts.length === 0 ? 'No records.' : `${parts.join(', ')}.`;
}

/** All the reasons in one sentence. */
export function reasonsText(reasons: readonly string[]): string {
  return reasons.map(reasonText).join('; ');
}

function songName(target: NamedTarget): string {
  const song = target.song;
  if (song.shortcode !== null) {
    return song.title === null ? song.shortcode : `${song.shortcode} “${song.title}”`;
  }
  return song.title === null ? 'a new Song' : `new Song “${song.title}”`;
}

/** Where an import goes, in plain words: "New Song “X”", "Version n8-1-v2 of n8-1 “X”", "New Version 4 of …". */
export function targetText(target: NamedTarget): string {
  switch (target.kind) {
    case 'newSong':
      return `New Song “${target.song.title ?? ''}”`;
    case 'newVersion':
      return `New Version ${target.number ?? ''} of ${songName(target)}${
        target.parent === null ? '' : `, from ${target.parent.shortcode}`
      }`;
    default:
      return target.version === null
        ? 'A Version that no longer exists'
        : `Version ${target.version.shortcode} of ${songName(target)}`;
  }
}

/** How each diffed provider field is named (#141). */
export const FIELD_LABELS: Record<string, string> = {
  title: 'Title',
  tags: 'Style tags',
  duration: 'Length',
  modelVersion: 'Model version',
  modelName: 'Model name',
  minimumBpm: 'Lowest BPM',
  maximumBpm: 'Highest BPM',
  averageBpm: 'Average BPM',
  key: 'Key',
  imageUrl: 'Cover image',
};

/** A diffed field's name in plain words. */
export function fieldLabel(field: string): string {
  return FIELD_LABELS[field] ?? field;
}

/** A diffed value in plain words: "None" when absent, a length as minutes and seconds. */
export function diffValueText(field: string, value: string | number | null): string {
  if (value === null || value === '') {
    return 'None';
  }
  if (typeof value === 'number') {
    return field === 'duration' ? durationText(value) : String(value);
  }
  return value;
}

/** The fields named, in plain words: "Title and Style tags". */
export function fieldsText(fields: readonly string[]): string {
  const labels = fields.map(fieldLabel);
  return labels.length <= 1
    ? (labels[0] ?? '')
    : `${labels.slice(0, -1).join(', ')} and ${labels[labels.length - 1] ?? ''}`;
}

/** Whether a record's diff can be reviewed: a Changed or Conflict record (#141). */
export function hasDiff(record: ImportRecord): boolean {
  return record.class === 'changed' || record.class === 'conflict';
}

/** What will happen to a Changed or Conflict record as its choice stands (#141). */
function resolutionText(record: ImportRecord): string {
  const choice = record.choice;
  if (choice === null || !('acceptFields' in choice)) {
    return 'Left as it is';
  }
  const taken =
    choice.acceptFields.length === 0 ? '' : `take ${fieldsText(choice.acceptFields)} from Suno`;
  switch (choice.action) {
    case 'moveToNewVersion':
      return `Move to a new Version${taken === '' ? '' : `, and ${taken}`}`;
    case 'keep':
      return `Keep it where it is${taken === '' ? '' : `, and ${taken}`}`;
    default:
      return taken === '' ? 'Keep n8Tracks’ data' : `T${taken.slice(1)}`;
  }
}

/** What will happen to a record as its choice stands, in plain words. */
export function choiceText(record: ImportRecord): string {
  const choice = record.choice;
  if (hasDiff(record)) {
    return resolutionText(record);
  }
  if (!isReviewable(record)) {
    return record.class === 'linked' ? 'Already in n8Tracks' : 'Left as it is';
  }
  if (choice === null || choice.action === 'skip') {
    // A record on the ignore list stays there unless it is imported.
    return record.class === 'ignored' ? 'Don’t copy' : 'Skip this time';
  }
  if (choice.action === 'ignore') {
    return 'Don’t copy';
  }
  const where = record.target === null ? 'Import' : targetText(record.target);
  return record.class === 'deleted' ? `Reimport: ${where}` : where;
}

/** Why a record that is not imported is shown as it is, when there is something to say. */
export function choiceNote(record: ImportRecord): string | undefined {
  if (record.class === 'ignored' && record.choice?.action !== 'import') {
    return 'It is on the ignore list.';
  }
  if (record.class === 'deleted' && record.choice?.action !== 'import') {
    return 'Its Generation was deleted in n8Tracks.';
  }
  if (record.class === 'changed') {
    return 'Suno’s data for it changed. Review the differences to choose what to take.';
  }
  if (record.class === 'conflict') {
    return 'It was made with other inputs than its Version. Review the differences to choose.';
  }
  return undefined;
}

/**
 * The group a record is listed under: its import target's key (a new Song or Version), or the existing
 * Version's ID; null for a record that is not imported (listed on its own).
 */
export function groupKey(record: ImportRecord): string | null {
  const choice: ImportChoice | null = record.choice;
  if (choice?.action !== 'import') {
    return null;
  }
  const target = choice.target;
  return target.kind === 'version' ? `version:${target.version}` : `key:${target.key}`;
}

/** A run of records under one heading (a target), or a record alone. */
export interface RecordGroup {
  key: string;
  heading: string | null;
  records: ImportRecord[];
}

/**
 * The page's records grouped by target, each group where its first record is: records proposed for the
 * same new Song or Version, or the same Version, are listed together under a heading for that target.
 */
export function groupRecords(records: readonly ImportRecord[]): RecordGroup[] {
  const groups: RecordGroup[] = [];
  const byKey = new Map<string, RecordGroup>();
  for (const record of records) {
    const key = groupKey(record);
    if (key === null) {
      groups.push({ key: `record:${record.sunoId}`, heading: null, records: [record] });
      continue;
    }
    const known = byKey.get(key);
    if (known !== undefined) {
      known.records.push(record);
      continue;
    }
    const group: RecordGroup = {
      key,
      heading: record.target === null ? 'Import' : targetText(record.target),
      records: [record],
    };
    byKey.set(key, group);
    groups.push(group);
  }
  return groups;
}

/** A record's duration as m:ss. */
export function durationText(seconds: number | null): string {
  if (seconds === null) {
    return '';
  }
  const whole = Math.round(seconds);
  return `${String(Math.floor(whole / 60))}:${String(whole % 60).padStart(2, '0')}`;
}

/** The review's filters read from the address's query (`class`, `workspace`, `playlist`, `q`). */
export function filterFrom(parameters: URLSearchParams): ImportFilter {
  const filter: ImportFilter = {};
  const recordClass = parameters.get('class');
  if (recordClass !== null && RECORD_CLASSES.includes(recordClass as RecordClass)) {
    filter.class = recordClass as RecordClass;
  }
  const workspace = parameters.get('workspace');
  if (workspace !== null && workspace !== '') {
    filter.workspace = workspace;
  }
  const playlist = parameters.get('playlist');
  if (playlist !== null && playlist !== '') {
    filter.playlist = playlist;
  }
  const q = parameters.get('q');
  if (q !== null && q !== '') {
    filter.q = q;
  }
  return filter;
}

/** What an export that is not open for review shows, by state. */
export function stateText(state: ImportState): { title: string; text: string } {
  switch (state) {
    case 'receiving':
      return {
        title: 'This sync is still being read',
        text: 'The n8Tracks extension is still sending this export. Its review opens once it is complete.',
      };
    case 'classifying':
      return {
        title: 'This import is being prepared',
        text: 'n8Tracks is comparing the records with your catalog. This page opens the review when it is ready.',
      };
    case 'committing':
      return {
        title: 'This import is being confirmed',
        text: 'n8Tracks is copying the chosen records into your catalog.',
      };
    case 'committed':
      return {
        title: 'This import was confirmed',
        text: 'Its records were copied into your catalog as chosen.',
      };
    case 'discarded':
      return {
        title: 'This import was discarded',
        text: 'Nothing in your catalog changed. Its records are still in Suno: sync again to review them.',
      };
    case 'failed':
      return {
        title: 'This import could not be prepared',
        text: 'Nothing in your catalog changed. Sync again from the n8Tracks extension on Suno.',
      };
    case 'expired':
      return {
        title: 'This import expired',
        text: 'It waited for review for more than seven days and was thrown away; nothing in your catalog changed. Sync again to review the records.',
      };
    default:
      return { title: 'This import is ready', text: '' };
  }
}

/** "1 record" or "N records". */
export function recordCountText(count: number): string {
  return count === 1 ? '1 record' : `${count.toLocaleString('en')} records`;
}

/** "1 Song", "2 Versions"…: `count` of `noun`, plural with an s. */
export function countText(count: number, noun: string): string {
  return `${count.toLocaleString('en')} ${noun}${count === 1 ? '' : 's'}`;
}
