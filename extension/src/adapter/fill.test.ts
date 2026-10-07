// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { fakeClock, loadSnapshot } from '../testing/snapshots.ts';
import { standInForSuno, type StandIn } from '../testing/sunoForm.ts';
import { FIELD_MAP } from './fieldMap.ts';
import {
  BLOCKED_ON_CAPTURE,
  FILLERS,
  normalisedText,
  verificationReport,
  verifyForm,
  WORKSPACE_ENTRY,
  type EntryResult,
  type Filler,
  type FormJob,
  fillEntry,
} from './fill.ts';
import type { Page } from './primitives.ts';

/**
 * The Create form fillers (#146 Songs, #147 Speech and Sounds) against the TS-003 snapshots, with Suno's behaviour stood in
 * (`testing/sunoForm.ts`): each filler sets its control and reads it back; with the control made
 * to keep another value, the entry is failed and the next is still filled; with the control gone,
 * it is unavailable.
 */

const ADVANCED = 'create-songs-advanced-more-options';
const SIMPLE = 'create-source-simple';
const SPEECH_SIMPLE = 'create-speech-simple';
const SPEECH_ADVANCED = 'create-speech-advanced';
const SOUNDS = 'create-sounds-advanced-options';

/** The field map's tab of each kind. */
const TAB: Readonly<Record<string, string>> = { song: 'songs', speech: 'speech', sound: 'sounds' };

let standIn: StandIn;
let page: Page;

function load(snapshot: string): Page {
  page = loadSnapshot(snapshot, 'https://suno.com/create', fakeClock());
  standIn = standInForSuno(document);
  return page;
}

beforeEach(() => {
  standIn = { commands: [], stop: () => undefined };
});

afterEach(() => {
  standIn.stop();
  document.body.innerHTML = '';
});

/** A job of `kind` (a Song unless given) in `mode`, with `entries` keyed by field, not by entry. */
function job(
  mode: string,
  entries: Record<string, unknown>,
  change: Partial<FormJob> = {},
  kind = 'song',
): FormJob {
  return {
    kind,
    mode,
    entries: Object.fromEntries(
      Object.entries(entries).map(([field, value]) => [
        `${TAB[kind] ?? kind}.${mode}.${field}`,
        value,
      ]),
    ),
    sources: [],
    fileInputs: [],
    unsupported: [],
    ...change,
  };
}

/** Values unlike the snapshots' own, so every filler has something to change. */
const ADVANCED_VALUES: Record<string, unknown> = {
  model: 'v6-mini',
  lyrics: '[Verse]\nfirst line\n\nsecond line',
  styles: 'dream pop, shoegaze',
  exclude_styles: 'metal',
  vocal_gender: 'male',
  duration_mode: 'custom',
  duration_seconds: 120,
  max_mode: false,
  weirdness: 55,
  style_influence: 45,
  variety: 'max',
  personalize: false,
  title: 'Night Drive',
};

const SIMPLE_VALUES: Record<string, unknown> = {
  model: 'v6-mini',
  simple_prompt: 'a quiet song about trains',
  simple_add_lyrics: null,
  simple_add_styles: null,
};

/** Speech and Sounds values unlike the snapshots' own (Female, Background music Off, Loop, 120). */
const SPEECH_SIMPLE_VALUES: Record<string, unknown> = { speech_prompt: 'a calm narrator' };

const SPEECH_ADVANCED_VALUES: Record<string, unknown> = {
  speech_script: 'Hello there.\nGoodbye.',
  speech_tone: 'warm and slow',
  speech_vocal_gender: 'male',
  speech_background_music: true,
  speech_variety: 'max',
};

const SOUND_VALUES: Record<string, unknown> = {
  sounds_model: 'v6-mini',
  sound_description: 'rain on a tin roof',
  sound_type: 'one_shot',
  sound_bpm: 90,
  sound_key: 'A',
  sound_scale: 'minor',
};

/** The snapshot and values each filler is tested with. */
function caseOf(filler: Filler): { snapshot: string; job: FormJob } {
  const [tab, mode] = filler.entry.split('.');
  if (tab === 'speech') {
    return mode === 'simple'
      ? { snapshot: SPEECH_SIMPLE, job: job('simple', SPEECH_SIMPLE_VALUES, {}, 'speech') }
      : { snapshot: SPEECH_ADVANCED, job: job('advanced', SPEECH_ADVANCED_VALUES, {}, 'speech') };
  }
  if (tab === 'sounds') {
    return { snapshot: SOUNDS, job: job('single', SOUND_VALUES, {}, 'sound') };
  }
  return mode === 'simple'
    ? { snapshot: SIMPLE, job: job('simple', SIMPLE_VALUES) }
    : { snapshot: ADVANCED, job: job('advanced', ADVANCED_VALUES) };
}

function byKey(results: readonly EntryResult[]): Map<string, EntryResult> {
  return new Map(results.map((result) => [result.key, result]));
}

// The test's own reach into the snapshot, to break or remove a control as Suno might.
function sectionOf(header: RegExp): Element {
  const found = [...document.querySelectorAll('[role="button"][aria-expanded]')].find((element) =>
    header.test(element.textContent.trim()),
  );
  if (found === undefined) {
    throw new Error(`no section ${header.source}`);
  }
  let current: Element | null = found.parentElement;
  while (current !== null && current.querySelector('input, textarea, [role="slider"]') === null) {
    current = current.parentElement;
  }
  if (current === null) {
    throw new Error(`no controls around ${header.source}`);
  }
  return current;
}

function moreOptions(): Element {
  return sectionOf(/^More Options/);
}

function labelled(text: string): Element[] {
  const label = [...moreOptions().querySelectorAll('span')].find(
    (span) => span.textContent.trim() === text,
  );
  return [...(label?.parentElement?.parentElement?.querySelectorAll('button') ?? [])].filter(
    (button) => ['Male', 'Female', 'Off', 'On'].includes(button.textContent.trim()),
  );
}

function slider(name: string): Element[] {
  return [...moreOptions().querySelectorAll(`[role="slider"][aria-label="${name}"]`)];
}

/** The Speech form's Advanced section, told from Sounds' Advanced Options. */
function speechAdvanced(): Element {
  return sectionOf(/^Advanced(?! Options)/);
}

function advancedOptions(): Element {
  return sectionOf(/^Advanced Options/);
}

/** The choice buttons beside a label inside a section (Speech's and Sounds' own). */
function labelledIn(section: Element, text: string): Element[] {
  const label = [...section.querySelectorAll('span')].find(
    (span) => span.textContent.trim() === text,
  );
  return [...(label?.parentElement?.parentElement?.querySelectorAll('button') ?? [])].filter(
    (button) =>
      ['Male', 'Female', 'Off', 'On', 'One-Shot', 'Loop'].includes(button.textContent.trim()),
  );
}

/** The elements of each filler's control in the snapshot. */
const CONTROLS: Readonly<Record<string, () => Element[]>> = {
  'songs.simple.simple_prompt': () => [...document.querySelectorAll('textarea')],
  'songs.advanced.lyrics': () => [...document.querySelectorAll('[aria-label="Lyrics editor"]')],
  'songs.advanced.styles': () => [...sectionOf(/^Styles/).querySelectorAll('textarea')],
  'songs.advanced.exclude_styles': () => [
    ...moreOptions().querySelectorAll('input[placeholder="Exclude styles"]'),
  ],
  'songs.advanced.vocal_gender': () => labelled('Vocal Gender'),
  'songs.advanced.duration_seconds': () => slider('Duration'),
  'songs.advanced.max_mode': () => labelled('Max Mode'),
  'songs.advanced.weirdness': () => slider('Weirdness'),
  'songs.advanced.style_influence': () => slider('Style Influence'),
  'songs.advanced.variety': () => slider('Variety'),
  'songs.advanced.personalize': () => labelled('Personalize'),
  'songs.advanced.title': () => [
    ...document.querySelectorAll('input[placeholder="Song Title (Optional)"]'),
  ],
  'speech.simple.speech_prompt': () => [...document.querySelectorAll('[aria-label="Prompt"]')],
  'speech.advanced.speech_script': () => [...document.querySelectorAll('[aria-label="Script"]')],
  'speech.advanced.speech_tone': () => [...document.querySelectorAll('[aria-label="Tone"]')],
  'speech.advanced.speech_vocal_gender': () => labelledIn(speechAdvanced(), 'Vocal Gender'),
  'speech.advanced.speech_background_music': () => labelledIn(speechAdvanced(), 'Background music'),
  'speech.advanced.speech_variety': () => [
    ...speechAdvanced().querySelectorAll('[role="slider"][aria-label="Variety"]'),
  ],
  'sounds.single.sound_description': () => [
    ...document.querySelectorAll('textarea[placeholder="Describe the sound you want"]'),
  ],
  'sounds.single.sound_type': () => labelledIn(advancedOptions(), 'Type'),
  'sounds.single.sound_bpm': () => [...advancedOptions().querySelectorAll('input[type="number"]')],
};

/** How each filler's control is made to keep another value, as a control Suno did not let change would. */
const BREAKERS: Readonly<Record<string, (controls: Element[]) => void>> = {
  text: (controls) => {
    for (const control of controls) {
      control.addEventListener('input', () => {
        (control as HTMLInputElement).value = 'what Suno kept';
      });
    }
  },
  click: (controls) => {
    for (const control of controls) {
      control.addEventListener('click', (event) => {
        event.stopPropagation();
      });
    }
  },
  keys: (controls) => {
    for (const control of controls) {
      control.addEventListener('keydown', (event) => {
        event.stopPropagation();
      });
    }
  },
  editor: () => {
    Object.defineProperty(document, 'execCommand', { configurable: true, value: () => false });
  },
};

const BREAK: Readonly<Record<string, keyof typeof BREAKERS>> = {
  'songs.simple.simple_prompt': 'text',
  'songs.advanced.lyrics': 'editor',
  'songs.advanced.styles': 'text',
  'songs.advanced.exclude_styles': 'text',
  'songs.advanced.vocal_gender': 'click',
  'songs.advanced.duration_seconds': 'keys',
  'songs.advanced.max_mode': 'click',
  'songs.advanced.weirdness': 'keys',
  'songs.advanced.style_influence': 'keys',
  'songs.advanced.variety': 'keys',
  'songs.advanced.personalize': 'click',
  'songs.advanced.title': 'text',
  'speech.simple.speech_prompt': 'text',
  'speech.advanced.speech_script': 'text',
  'speech.advanced.speech_tone': 'text',
  'speech.advanced.speech_vocal_gender': 'click',
  'speech.advanced.speech_background_music': 'click',
  'speech.advanced.speech_variety': 'keys',
  'sounds.single.sound_description': 'text',
  'sounds.single.sound_type': 'click',
  'sounds.single.sound_bpm': 'text',
};

/**
 * What the coverage test requires (#146 AC 8, #147 AC 4): every `fill` entry of the field map, on
 * every tab, has a filler, a success case, and a read-back failure case, except the workspace (the
 * workspace story's) and the entries blocked on a capture (D9), which have none.
 */
function coverageProblems(fillers: readonly Filler[]): string[] {
  const problems: string[] = [];
  for (const { entry, how } of FIELD_MAP) {
    if (how !== 'fill' || entry === WORKSPACE_ENTRY) {
      continue;
    }
    const filler = fillers.filter((candidate) => candidate.entry === entry);
    if (BLOCKED_ON_CAPTURE.has(entry)) {
      if (filler.length > 0) {
        problems.push(`${entry}: has a filler but is listed as blocked on a capture`);
      }
      continue;
    }
    if (filler.length !== 1) {
      problems.push(`${entry}: has ${String(filler.length)} fillers, not one`);
    }
    if (CONTROLS[entry] === undefined || BREAK[entry] === undefined) {
      problems.push(`${entry}: has no success and read-back failure case`);
    }
  }
  return problems;
}

describe('the Songs form fillers', () => {
  it.each(FILLERS.map((filler) => [filler.entry, filler] as const))(
    '%s: sets its control and reads it back',
    async (_entry, filler) => {
      const tested = caseOf(filler);
      load(tested.snapshot);

      const results = byKey(await verifyForm(page, tested.job, 'My Workspace', true));

      expect(results.get(filler.entry)).toMatchObject({ outcome: 'set' });
    },
  );

  it.each(FILLERS.map((filler) => [filler.entry, filler] as const))(
    '%s: is failed when the control keeps another value, and the next entry is still filled',
    async (_entry, filler) => {
      const tested = caseOf(filler);
      load(tested.snapshot);
      const breaker = BREAK[filler.entry];
      expect(breaker).toBeDefined();
      BREAKERS[breaker ?? 'text']?.(CONTROLS[filler.entry]?.() ?? []);
      const results = await verifyForm(page, tested.job, 'My Workspace', true);

      const broken = byKey(results).get(filler.entry);
      expect(broken?.outcome).toBe('failed');
      expect(broken?.expected).toBeDefined();
      expect(broken?.found).toBeDefined();
      expect(broken?.found).not.toEqual(broken?.expected);
      // Every other filler of the mode was still attempted and set.
      const others = FILLERS.filter(
        (other) =>
          other.entry !== filler.entry &&
          other.entry.startsWith(filler.entry.split('.', 2).join('.')),
      );
      for (const other of others) {
        expect(byKey(results).get(other.entry)?.outcome, other.entry).toBe('set');
      }
    },
  );

  it.each(FILLERS.map((filler) => [filler.entry, filler] as const))(
    '%s: is unavailable when its control is not on the form',
    async (_entry, filler) => {
      const tested = caseOf(filler);
      load(tested.snapshot);
      for (const control of CONTROLS[filler.entry]?.() ?? []) {
        control.remove();
      }

      const result = byKey(await verifyForm(page, tested.job, null, true)).get(filler.entry);

      expect(result?.outcome).toBe('unavailable');
      expect(result?.note).toMatch(/does not show/);
    },
  );

  it('resets options left from an earlier use: every entry ends at the Version’s value, defaults included', async () => {
    // The snapshot has Female, Max Mode and Personalize On, Weirdness 70, Variety High, and text.
    load(ADVANCED);
    const defaults = job('advanced', {
      model: 'v6-mini',
      lyrics: '',
      styles: '',
      exclude_styles: '',
      vocal_gender: null,
      duration_mode: 'custom',
      duration_seconds: 180,
      max_mode: false,
      weirdness: 50,
      style_influence: 50,
      variety: 'normal',
      personalize: false,
      title: '',
    });

    const results = byKey(await verifyForm(page, defaults, 'My Workspace', true));

    for (const filler of FILLERS.filter((item) => item.entry.startsWith('songs.advanced.'))) {
      expect(results.get(filler.entry)?.outcome, filler.entry).toBe('set');
    }
    expect(results.get('songs.advanced.vocal_gender')).toMatchObject({ expected: null });
    expect(standIn.commands).toEqual(['delete']);
    // Read again without changing anything, the form now says the same.
    const again = byKey(await verifyForm(page, defaults, 'My Workspace', false));
    expect([...again.values()].filter((result) => result.outcome === 'failed')).toEqual([]);
  });

  it('types the lyrics line by line, and compares them after normalising line ends and trailing space', async () => {
    load(ADVANCED);
    const lyrics = job('advanced', { ...ADVANCED_VALUES, lyrics: 'one  \r\ntwo\r\n\r\nthree\n' });

    const results = byKey(await verifyForm(page, lyrics, null, true));

    expect(results.get('songs.advanced.lyrics')?.outcome).toBe('set');
    expect(standIn.commands).toEqual([
      'insertText',
      'insertParagraph',
      'insertText',
      'insertParagraph',
      'insertParagraph',
      'insertText',
      'insertParagraph',
    ]);
    expect(normalisedText('one  \r\ntwo\n\n')).toBe('one\ntwo');
  });

  it('sets a Duration that is not a multiple of 5 seconds to the nearest, and reports it failed with what was found', async () => {
    load(ADVANCED);

    const results = byKey(
      await verifyForm(
        page,
        job('advanced', { ...ADVANCED_VALUES, duration_seconds: 33 }),
        null,
        true,
      ),
    );

    expect(results.get('songs.advanced.duration_seconds')).toMatchObject({
      outcome: 'failed',
      expected: 33,
      found: 35,
    });
  });

  it('leaves the Duration length alone under Auto (not applicable)', async () => {
    load(ADVANCED);

    const results = byKey(
      await verifyForm(
        page,
        job('advanced', { ...ADVANCED_VALUES, duration_mode: 'auto' }),
        null,
        true,
      ),
    );

    expect(results.get('songs.advanced.duration_seconds')?.outcome).toBe('not_applicable');
    expect(slider('Duration')[0]?.getAttribute('aria-valuenow')).toBe('30');
  });

  it('deselects Vocal Gender for None, and reports it failed when it cannot', async () => {
    load(ADVANCED);
    BREAKERS.click?.(labelled('Vocal Gender'));

    const results = byKey(
      await verifyForm(
        page,
        job('advanced', { ...ADVANCED_VALUES, vocal_gender: null }),
        null,
        true,
      ),
    );

    expect(results.get('songs.advanced.vocal_gender')).toMatchObject({
      outcome: 'failed',
      expected: null,
      found: 'female',
    });
  });

  // #339: no snapshot shows the model menu open, so the model is never chosen from it.
  it('reports the model as to choose by hand, pressing nothing for it (blocked on a capture)', async () => {
    load(ADVANCED);
    const pressed: string[] = [];
    document.addEventListener(
      'click',
      (event) => {
        pressed.push((event.target as Element).getAttribute('aria-haspopup') ?? '');
      },
      { capture: true },
    );

    const results = byKey(
      await verifyForm(page, job('advanced', { ...ADVANCED_VALUES, model: 'v6-wild' }), null, true),
    );

    expect(results.get('songs.advanced.model')).toMatchObject({
      outcome: 'manual',
      expected: 'v6-wild',
      note: 'Choose the model in Suno’s model menu by hand: the extension cannot use the model menu yet.',
    });
    expect(pressed).not.toContain('menu');
    expect(document.querySelector('[role="menu"]')).toBeNull();

    const none = byKey(
      await verifyForm(page, job('advanced', { ...ADVANCED_VALUES, model: null }), null, false),
    );
    expect(none.get('songs.advanced.model')?.outcome).toBe('not_applicable');
  });

  it('never presses Create, and presses nothing outside the form’s own controls', async () => {
    load(ADVANCED);
    const pressed: string[] = [];
    document.addEventListener(
      'click',
      (event) => {
        pressed.push((event.target as Element).textContent.trim());
      },
      { capture: true },
    );

    await verifyForm(page, job('advanced', ADVANCED_VALUES), null, true);

    expect(pressed.sort()).toEqual(['Male', 'Off', 'Off']);
  });
});

describe('the summary’s other entries', () => {
  it('lists every entry of Simple mode: the workspace set, sources and files to do by hand, sections blocked on a capture', async () => {
    load(SIMPLE);
    const simple = job(
      'simple',
      { ...SIMPLE_VALUES, simple_add_lyrics: 'words', voice: { name: 'Ada' } },
      {
        sources: [{ key: 'songs.simple.audio', title: 'Origin', sunoAction: 'cover' }],
        fileInputs: [{ key: 'songs.simple.simple_add_image', description: 'the cover photo' }],
        unsupported: ['songs.simple.crop'],
      },
    );

    const results = await verifyForm(page, simple, 'My Workspace', true);

    expect(results.map((result) => [result.key, result.outcome])).toEqual([
      ['songs.simple.model', 'manual'],
      ['songs.simple.simple_prompt', 'set'],
      ['songs.simple.simple_add_lyrics', 'manual'],
      ['songs.simple.simple_add_styles', 'not_applicable'],
      ['songs.simple.simple_add_playlist', 'not_applicable'],
      ['songs.simple.simple_add_image', 'manual'],
      ['songs.simple.simple_add_video', 'not_applicable'],
      ['songs.simple.audio', 'manual'],
      ['songs.simple.voice', 'manual'],
      ['songs.simple.workspace', 'set'],
      ['songs.simple.crop', 'unsupported'],
    ]);
    const notes = byKey(results);
    expect(notes.get('songs.simple.simple_add_image')?.note).toBe(
      'Attach the file by hand: the cover photo.',
    );
    // No Suno ID: a Song-level source, which only the user can load (#148).
    expect(notes.get('songs.simple.audio')?.note).toContain('Load “Origin” by hand');
    expect(notes.get('songs.simple.voice')?.note).toContain('Choose the voice “Ada”');
    expect(notes.get('songs.simple.simple_add_lyrics')?.note).toMatch(/cannot add Simple’s Lyrics/);
  });

  it.each(
    FIELD_MAP.filter((entry) => entry.entry.startsWith('songs.') && entry.how === 'manual').map(
      (entry) => [entry.entry] as const,
    ),
  )('%s (manual) is to do by hand, with the Version’s note', async (entry) => {
    load(SIMPLE);
    const mode = entry.split('.')[1] ?? 'simple';
    const withFile = job(mode, {}, { fileInputs: [{ key: entry, description: 'the note' }] });

    const result = byKey(await verifyForm(page, withFile, null, false)).get(entry);

    expect(result).toEqual({
      key: entry,
      outcome: 'manual',
      note: 'Attach the file by hand: the note.',
      reportNote: 'Attach the file by hand: the Version’s file note says which.',
    });
  });

  it('lists the workspace in Advanced mode too, and Duration’s mode as to do by hand', async () => {
    load(ADVANCED);

    const results = byKey(await verifyForm(page, job('advanced', ADVANCED_VALUES), 'Studio', true));

    expect(results.get(WORKSPACE_ENTRY)?.outcome).toBe('set');
    expect(results.get('songs.advanced.duration_mode')).toMatchObject({
      outcome: 'manual',
      expected: 'custom',
    });
  });
});

describe('the coverage of the fill entries on every tab (#146 AC 8, #147 AC 4)', () => {
  it('has a filler with a success and a read-back failure case for every fill entry', () => {
    expect(coverageProblems(FILLERS)).toEqual([]);
  });

  it('covers the Speech and Sounds tabs too', () => {
    const covered = FILLERS.map((filler) => filler.entry.split('.')[0]);

    expect(new Set(covered)).toEqual(new Set(['songs', 'speech', 'sounds']));
  });

  it.each([
    ['songs.advanced.lyrics'],
    ['speech.advanced.speech_tone'],
    ['sounds.single.sound_bpm'],
  ])('bites: with %s unregistered, the coverage test fails naming its entry', (entry) => {
    const without = FILLERS.filter((filler) => filler.entry !== entry);

    expect(coverageProblems(without)).toEqual([`${entry}: has 0 fillers, not one`]);
  });

  it('lists exactly the entries no TS-003 snapshot shows as blocked on a capture (D9)', () => {
    expect([...BLOCKED_ON_CAPTURE].sort()).toEqual([
      'songs.advanced.duration_mode',
      'songs.advanced.model',
      'songs.simple.model',
      'songs.simple.simple_add_lyrics',
      'songs.simple.simple_add_styles',
      'sounds.single.sound_key',
      'sounds.single.sound_scale',
      'sounds.single.sounds_model',
    ]);
  });
});

describe('the Speech and Sounds summaries (#147)', () => {
  it('lists only its one entry for a Simple Speech Version', async () => {
    load(SPEECH_SIMPLE);

    const results = await verifyForm(
      page,
      job('simple', SPEECH_SIMPLE_VALUES, {}, 'speech'),
      'My Workspace',
      true,
    );

    expect(results.map((result) => [result.key, result.outcome])).toEqual([
      ['speech.simple.speech_prompt', 'set'],
    ]);
  });

  it('lists every Advanced Speech entry as set, with nothing pressed outside the form', async () => {
    load(SPEECH_ADVANCED);
    const pressed: string[] = [];
    document.addEventListener(
      'click',
      (event) => {
        pressed.push((event.target as Element).textContent.trim());
      },
      { capture: true },
    );

    const results = await verifyForm(
      page,
      job('advanced', SPEECH_ADVANCED_VALUES, {}, 'speech'),
      null,
      true,
    );

    expect(results.map((result) => [result.key, result.outcome])).toEqual([
      ['speech.advanced.speech_script', 'set'],
      ['speech.advanced.speech_tone', 'set'],
      ['speech.advanced.speech_vocal_gender', 'set'],
      ['speech.advanced.speech_background_music', 'set'],
      ['speech.advanced.speech_variety', 'set'],
    ]);
    expect(pressed.sort()).toEqual(['Male', 'On']);
  });

  it('lists the six Sounds entries: three set, the model, Key and Key scale to do by hand (D9)', async () => {
    load(SOUNDS);

    const results = await verifyForm(page, job('single', SOUND_VALUES, {}, 'sound'), null, true);

    expect(results.map((result) => [result.key, result.outcome])).toEqual([
      ['sounds.single.sounds_model', 'manual'],
      ['sounds.single.sound_description', 'set'],
      ['sounds.single.sound_type', 'set'],
      ['sounds.single.sound_bpm', 'set'],
      ['sounds.single.sound_key', 'manual'],
      ['sounds.single.sound_scale', 'manual'],
    ]);
    const byEntry = byKey(results);
    expect(byEntry.get('sounds.single.sound_key')).toMatchObject({ expected: 'A' });
    expect(byEntry.get('sounds.single.sound_scale')).toMatchObject({ expected: 'minor' });
    expect(byEntry.get('sounds.single.sound_key')?.note).toMatch(/Key picker/);
  });

  it('does not ask for a Key scale when the Key is Any (not applicable)', async () => {
    load(SOUNDS);

    const results = byKey(
      await verifyForm(
        page,
        job('single', { ...SOUND_VALUES, sound_key: 'any', sound_scale: undefined }, {}, 'sound'),
        null,
        true,
      ),
    );

    expect(results.get('sounds.single.sound_scale')?.outcome).toBe('not_applicable');
    expect(results.get('sounds.single.sound_key')).toMatchObject({
      outcome: 'manual',
      expected: 'any',
    });
  });

  it('re-selects Auto for an empty BPM: the box is emptied', async () => {
    // The snapshot's BPM is 120.
    load(SOUNDS);

    const results = byKey(
      await verifyForm(
        page,
        job('single', { ...SOUND_VALUES, sound_bpm: null }, {}, 'sound'),
        null,
        true,
      ),
    );

    expect(results.get('sounds.single.sound_bpm')).toMatchObject({
      outcome: 'set',
      expected: null,
    });
    expect(
      CONTROLS['sounds.single.sound_bpm']?.().map((box) => (box as HTMLInputElement).value),
    ).toEqual(['']);
  });

  it('leaves BPM on Auto when it already is: nothing is typed', async () => {
    // The Speech snapshot holds the Sounds form at its defaults: One-Shot, BPM empty (Auto), Key Any.
    load(SPEECH_ADVANCED);
    const box = CONTROLS['sounds.single.sound_bpm']?.()[0];
    let typed = 0;
    box?.addEventListener('input', () => {
      typed += 1;
    });
    const bpm = FILLERS.find((filler) => filler.entry === 'sounds.single.sound_bpm');

    const result =
      bpm === undefined
        ? undefined
        : await fillEntry(page, bpm, job('single', { sound_bpm: null }, {}, 'sound'));

    expect(result).toMatchObject({ outcome: 'set', expected: null });
    expect(typed).toBe(0);
    expect((box as HTMLInputElement | undefined)?.value).toBe('');
  });

  it('attempts a BPM out of Suno’s range and reports it failed with what Suno kept', async () => {
    load(SOUNDS);
    // As Suno would clamp it to its maximum.
    for (const box of CONTROLS['sounds.single.sound_bpm']?.() ?? []) {
      box.addEventListener('input', () => {
        (box as HTMLInputElement).value = '300';
      });
    }

    const results = byKey(
      await verifyForm(
        page,
        job('single', { ...SOUND_VALUES, sound_bpm: 400 }, {}, 'sound'),
        null,
        true,
      ),
    );

    expect(results.get('sounds.single.sound_bpm')).toMatchObject({
      outcome: 'failed',
      expected: 400,
      found: 300,
    });
  });

  it('reports the Sound model as to choose by hand (#339: blocked on a capture)', async () => {
    load(SOUNDS);

    const results = byKey(
      await verifyForm(
        page,
        job('single', { ...SOUND_VALUES, sounds_model: 'v6-wild' }, {}, 'sound'),
        null,
        true,
      ),
    );

    expect(results.get('sounds.single.sounds_model')).toMatchObject({
      outcome: 'manual',
      expected: 'v6-wild',
    });
  });

  it('deselects the Speech Vocal Gender for None, and reports a Type Suno does not offer as failed', async () => {
    load(SPEECH_ADVANCED);

    const speech = byKey(
      await verifyForm(
        page,
        job('advanced', { ...SPEECH_ADVANCED_VALUES, speech_vocal_gender: null }, {}, 'speech'),
        null,
        true,
      ),
    );
    const sound = byKey(
      await verifyForm(
        page,
        job('single', { ...SOUND_VALUES, sound_type: 'drone' }, {}, 'sound'),
        null,
        true,
      ),
    );

    expect(speech.get('speech.advanced.speech_vocal_gender')).toMatchObject({
      outcome: 'set',
      expected: null,
    });
    expect(sound.get('sounds.single.sound_type')).toMatchObject({ outcome: 'failed' });
    expect(sound.get('sounds.single.sound_type')?.note).toMatch(/not one Suno’s form offers/);
  });

  it('keeps the other tabs’ controls out: the Songs Variety is never set for a Speech Version', async () => {
    load(SPEECH_ADVANCED);

    await verifyForm(page, job('advanced', SPEECH_ADVANCED_VALUES, {}, 'speech'), null, true);

    expect(slider('Variety')[0]?.getAttribute('aria-valuenow')).toBe('2');
    expect(CONTROLS['speech.advanced.speech_variety']?.()[0]?.getAttribute('aria-valuenow')).toBe(
      '4',
    );
  });
});

describe('the verification report', () => {
  it('sends text as its length and SHA-256 only, and other values as they are', async () => {
    const report = await verificationReport(
      [
        {
          key: 'songs.advanced.lyrics',
          outcome: 'failed',
          expected: 'abc',
          found: 'ab',
          text: true,
        },
        { key: 'songs.advanced.weirdness', outcome: 'set', expected: 55 },
        { key: 'songs.advanced.audio', outcome: 'manual', note: 'Load it by hand.' },
      ],
      'advanced',
      5,
      new Date('2026-10-07T12:00:00Z'),
    );

    expect(report).toEqual({
      adapterVersion: 5,
      mode: 'advanced',
      checkedAt: '2026-10-07T12:00:00.000Z',
      entries: [
        {
          key: 'songs.advanced.lyrics',
          outcome: 'failed',
          expected: {
            length: 3,
            sha256: 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad',
          },
          found: {
            length: 2,
            sha256: 'fb8e20fc2e4c3f248c60c39bd652f3c1347298bb977b8b4d5903b85055620603',
          },
        },
        { key: 'songs.advanced.weirdness', outcome: 'set', expected: 55 },
        { key: 'songs.advanced.audio', outcome: 'manual', note: 'Load it by hand.' },
      ],
    });
    expect(JSON.stringify(report)).not.toContain('"abc"');
  });

  it('sends the adapter’s words only: no source title, file note, or voice name the panel shows (#340)', async () => {
    load(SIMPLE);
    const simple = job(
      'simple',
      { ...SIMPLE_VALUES, voice: { name: 'Private Voice Name' } },
      {
        sources: [
          { key: 'songs.simple.audio', title: 'Private Source Title', sunoAction: 'cover' },
        ],
        fileInputs: [
          { key: 'songs.simple.simple_add_image', description: 'private cover photo note' },
          { key: 'songs.simple.audio', description: 'private bass take note' },
        ],
      },
    );
    const results = await verifyForm(page, simple, 'My Workspace', true);
    const privateText = [
      'Private Voice Name',
      'Private Source Title',
      'private cover photo note',
      'private bass take note',
    ];

    // The panel names them, so the user knows what to do by hand ...
    const shown = results.map((result) => result.note ?? '').join(' ');
    for (const text of privateText) {
      expect(shown).toContain(text);
    }

    // ... and n8Tracks gets the same steps naming them generically.
    const report = await verificationReport(results, 'simple', 5, new Date('2026-10-07T12:00:00Z'));
    const sent = JSON.stringify(report);
    for (const text of privateText) {
      expect(sent).not.toContain(text);
    }
    const notes = new Map(report.entries.map((entry) => [entry.key, entry.note]));
    expect(notes.get('songs.simple.simple_add_image')).toBe(
      'Attach the file by hand: the Version’s file note says which.',
    );
    expect(notes.get('songs.simple.audio')).toContain('Load the source by hand');
    expect(notes.get('songs.simple.audio')).toContain(
      'attach the audio file by hand (the Version’s file note says which)',
    );
    expect(notes.get('songs.simple.voice')).toContain('Choose the voice the Version names');
  });
});
