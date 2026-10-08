// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { stepsOfRun } from '../diagnostics/report.ts';
import { fakeClock, loadSnapshot, SNAPSHOT_NAMES, snapshotHtml } from '../testing/snapshots.ts';
import { standInForSuno } from '../testing/sunoForm.ts';
import { FIELD_MAP } from './fieldMap.ts';
import type { EntryResult, FormJob, FormSource } from './fill.ts';
import { nameOf, type Page } from './primitives.ts';
import {
  clipPageShows,
  extendSeconds,
  extendTime,
  holdsClip,
  INSPIRATION_ROUTE,
  planSources,
  SOURCE_ROUTES,
  sourceEntryResult,
  SOURCES_BLOCKED_ON_CAPTURE,
  type LoadedSource,
  type SourceRoute,
} from './sources.ts';
import { runWorkflow, type RunResult } from './workflow.ts';
import {
  answerOverwrite,
  chooseSourceAction,
  chooseVoice,
  openSourceMenu,
  setExtendFrom,
  verifySourceAdvanced,
  verifySourceSimple,
} from './workflows/sources.ts';
import { ADAPTER_WORKFLOWS } from './workflows/index.ts';

/** The source clips of the TS-003 snapshots: the Advanced Audio section's, and the Simple chip's. */
const ADVANCED_SOURCE = '00000000-0000-4000-8000-000000000104';
const SIMPLE_SOURCE = '00000000-0000-4000-8000-000000000109';
const OTHER_CLIP = '00000000-0000-4000-8000-000000000199';

const SONG_PAGE = `https://suno.com/song/${ADVANCED_SOURCE}`;

/** TS-005's clips: the source loaded on the Create form, the clip whose page was captured, the voice. */
const LOADED_SOURCE = '00000000-0000-4000-8000-000000000201';
const CLIP_PAGE_CLIP = '00000000-0000-4000-8000-000000000204';
const VOICE_PERSONA = '00000000-0000-4000-8000-000000000203';
/** The chosen voice's name, as the sanitized snapshots show it. */
const VOICE_NAME = '<redacted 13 chars>';

function source(change: Partial<FormSource> = {}): FormSource {
  return {
    key: 'songs.advanced.audio',
    title: 'Night Drive (demo)',
    sunoAction: 'cover',
    group: 'audio',
    position: 1,
    sunoId: ADVANCED_SOURCE,
    availability: 'ok',
    continueAtSeconds: null,
    ...change,
  };
}

function form(change: Partial<FormJob> = {}): FormJob {
  return {
    kind: 'song',
    mode: 'advanced',
    entries: {},
    sources: [source()],
    fileInputs: [],
    unsupported: [],
    ...change,
  };
}

function loaded(job: FormJob): LoadedSource {
  const load = planSources(job).load;
  if (load === null) {
    throw new Error('Expected a source to load.');
  }
  return load;
}

/** Every element a click reached, by accessible name. */
function recordPresses(): string[] {
  const pressed: string[] = [];
  document.addEventListener(
    'click',
    (event) => {
      pressed.push(nameOf(event.target as Element));
    },
    { capture: true },
  );
  return pressed;
}

async function run(
  page: Page,
  workflow: Parameters<typeof runWorkflow>[0],
  values: object,
): Promise<RunResult> {
  return runWorkflow(workflow, page, values, { clock: fakeClock(), pollMs: 1000 });
}

afterEach(() => {
  document.body.innerHTML = '';
});

describe('the source plan (#148)', () => {
  it('loads a Cover or a Reuse Prompt source from the clip’s Remix menu, by its Suno ID', () => {
    expect(planSources(form())).toEqual({
      stop: null,
      load: { source: source(), sunoId: ADVANCED_SOURCE, route: SOURCE_ROUTES.cover, others: [] },
    });
    expect(
      planSources(form({ sources: [source({ sunoAction: 'reuse_prompt' })] })).load?.route,
    ).toEqual({
      menu: 'Remix',
      item: 'Reuse Prompt',
      label: null,
      captured: true,
      simple: true,
      automated: true,
    });
  });

  // #341: the clip's own page is captured now (TS-005), so every route is taken by the extension.
  it('takes every route itself now that the clip’s page is captured (TS-005)', () => {
    expect(
      [...Object.values(SOURCE_ROUTES), INSPIRATION_ROUTE].filter((route) => !route.automated),
    ).toEqual([]);
    expect(SNAPSHOT_NAMES).toEqual(
      expect.arrayContaining(['clip-page', 'clip-page-remix-menu', 'clip-not-found']),
    );
  });

  it('loads a user type mapped to Cover (#126) as Cover: the extension knows only the action key', () => {
    const mapped = source({ title: 'Reinterpretation of Night Drive', sunoAction: 'cover' });

    expect(planSources(form({ sources: [mapped] })).load?.route).toBe(SOURCE_ROUTES.cover);
  });

  it('stops before changing the form, naming each source n8Tracks knows cannot be used (#142)', () => {
    const plan = planSources(
      form({
        sources: [
          source({ availability: 'trashed' }),
          source({
            group: 'inspiration',
            key: 'songs.advanced.inspiration',
            title: 'Gone',
            availability: 'missing',
          }),
        ],
      }),
    );

    expect(plan.load).toBeNull();
    expect(plan.stop).toBe(
      'The Version’s source “Night Drive (demo)” is in Suno’s Trash; “Gone” is no longer in Suno’s library, so nothing on Suno’s form was changed. Restore it in Suno and sync, or change the Version’s sources, then start again.',
    );
  });

  it('refuses more than four Inspiration songs, which Suno does not take', () => {
    const songs = [1, 2, 3, 4, 5].map((position) =>
      source({
        key: 'songs.advanced.inspiration',
        group: 'inspiration',
        sunoAction: null,
        position,
      }),
    );

    expect(planSources(form({ sources: songs })).stop).toContain('more than 4 songs');
    expect(planSources(form({ sources: songs.slice(0, 4) })).stop).toBeNull();
  });

  it('loads Extend, Sample, and a Mashup in Advanced mode (TS-005), a Mashup’s second song with the first', () => {
    expect(
      planSources(form({ sources: [source({ sunoAction: 'extend', continueAtSeconds: 42 })] })).load
        ?.route,
    ).toBe(SOURCE_ROUTES.extend);
    expect(planSources(form({ sources: [source({ sunoAction: 'sample' })] })).load?.route).toBe(
      SOURCE_ROUTES.sample,
    );
    const second = source({ sunoAction: 'mashup', position: 2, sunoId: OTHER_CLIP });
    const mashup = planSources(form({ sources: [second, source({ sunoAction: 'mashup' })] })).load;
    expect(mashup).toMatchObject({ sunoId: ADVANCED_SOURCE, route: SOURCE_ROUTES.mashup });
    expect(mashup?.others).toEqual([second]);
  });

  it('loads the first Inspiration song when the Version has no audio source, the others with it', () => {
    const songs = [1, 2].map((position) =>
      source({
        key: 'songs.advanced.inspiration',
        group: 'inspiration',
        sunoAction: null,
        position,
        sunoId: position === 1 ? ADVANCED_SOURCE : OTHER_CLIP,
      }),
    );

    const load = planSources(form({ sources: songs })).load;

    expect(load).toMatchObject({ sunoId: ADVANCED_SOURCE, route: INSPIRATION_ROUTE });
    expect(load?.others).toEqual([songs[1]]);
    // With an audio source, the audio source is loaded, and Inspiration is left to the user.
    expect(planSources(form({ sources: [source(), ...songs] })).load?.route).toBe(
      SOURCE_ROUTES.cover,
    );
  });

  it('loads nothing it cannot verify: an action Simple’s form was not captured after, a Song-level source, a general Remix, two sources that are not a Mashup', () => {
    for (const job of [
      form({
        mode: 'simple',
        sources: [source({ key: 'songs.simple.audio', sunoAction: 'extend' })],
      }),
      form({
        mode: 'simple',
        sources: [source({ key: 'songs.simple.audio', sunoAction: 'sample' })],
      }),
      form({ sources: [source({ sunoId: null })] }),
      form({ sources: [source({ sunoAction: null })] }),
      form({ sources: [source(), source({ position: 2, sunoId: OTHER_CLIP })] }),
      form({
        sources: [
          source({ sunoAction: 'mashup' }),
          source({ sunoAction: 'mashup', position: 2, sunoId: null }),
        ],
      }),
    ]) {
      expect(planSources(job)).toEqual({ stop: null, load: null });
    }
  });
});

describe('the summary lines of the source entries (#148)', () => {
  it('names what is to do by hand, and why, for every source the extension does not load', () => {
    const job = form({
      sources: [
        source({ sunoAction: 'extend', continueAtSeconds: 42 }),
        source({ sunoAction: 'extend', position: 2, sunoId: OTHER_CLIP, title: 'Coda' }),
        source({
          key: 'songs.advanced.inspiration',
          group: 'inspiration',
          sunoAction: null,
          title: 'Rain',
        }),
      ],
      entries: { 'songs.advanced.voice': { personaId: 'p-1', name: 'Velvet' } },
    });

    expect(sourceEntryResult('songs.advanced.audio', job)).toEqual({
      key: 'songs.advanced.audio',
      outcome: 'manual',
      note: 'Load “Night Drive (demo)” with Edit › Extend by hand: the extension does not load it in this mode, or with these other sources; load “Coda” with Edit › Extend by hand: the extension does not load it in this mode, or with these other sources.',
      reportNote:
        'Load the source with Edit › Extend by hand: the extension does not load it in this mode, or with these other sources; load the source with Edit › Extend by hand: the extension does not load it in this mode, or with these other sources.',
    });
    expect(sourceEntryResult('songs.advanced.inspiration', job)).toMatchObject({
      outcome: 'manual',
      note: 'Add “Rain” as Inspiration (Use as Inspiration) by hand: the extension loads Inspiration only when the Version has no audio source.',
    });
    // A voice not chosen by the extension is to do by hand with its name, never failed.
    expect(sourceEntryResult('songs.advanced.voice', job)).toEqual({
      key: 'songs.advanced.voice',
      outcome: 'manual',
      note: 'Choose the voice “Velvet” from + Voice by hand (the extension did not choose it).',
      reportNote:
        'Choose the voice the Version names from + Voice by hand (the extension did not choose it).',
    });
    // The voice the extension chose gives its own outcome.
    const chosen: EntryResult = {
      key: 'songs.advanced.voice',
      outcome: 'verified',
      note: 'Chosen.',
    };
    expect(sourceEntryResult('songs.advanced.voice', { ...job, voice: chosen })).toEqual(chosen);
  });

  it('names an action it does not know in the panel only, never in the note n8Tracks stores (#379)', () => {
    const job = form({ sources: [source({ sunoAction: 'my_remix', title: 'Night Drive' })] });

    expect(sourceEntryResult('songs.advanced.audio', job)).toEqual({
      key: 'songs.advanced.audio',
      outcome: 'manual',
      note: 'Load “Night Drive” as my_remix by hand: the extension does not know that action.',
      reportNote: 'Load the source by hand: the extension does not know its Suno action.',
    });
  });

  it('lists a playlist, a Song-level source, and an audio file note as to do by hand', () => {
    const simple = form({
      mode: 'simple',
      sources: [source({ key: 'songs.simple.audio', sunoId: null, title: 'Demo Song' })],
      entries: {
        'songs.simple.simple_add_playlist': { sunoPlaylistId: 'pl-1', name: 'Late nights' },
      },
      fileInputs: [{ key: 'songs.simple.audio', description: 'the bass take' }],
    });

    expect(sourceEntryResult('songs.simple.audio', simple).note).toBe(
      'Load “Demo Song” by hand: it is a Song in n8Tracks, not a Suno clip; attach the audio file by hand (the bass take).',
    );
    // n8Tracks keeps the same steps without the Version's text (#340).
    expect(sourceEntryResult('songs.simple.audio', simple).reportNote).toBe(
      'Load the source by hand: it is a Song in n8Tracks, not a Suno clip; attach the audio file by hand (the Version’s file note says which).',
    );
    expect(sourceEntryResult('songs.simple.simple_add_playlist', simple).reportNote).toMatch(
      /^Add the playlist the Version names from \+ Inspo by hand/,
    );
    expect(sourceEntryResult('songs.simple.simple_add_playlist', simple)).toMatchObject({
      outcome: 'manual',
      note: expect.stringContaining(
        'Add the playlist “Late nights” from + Inspo by hand',
      ) as string,
    });
    expect(sourceEntryResult('songs.simple.voice', simple)).toEqual({
      key: 'songs.simple.voice',
      outcome: 'not_applicable',
    });
  });

  it('gives the loaded source its own outcome', () => {
    const verified: EntryResult = {
      key: 'songs.advanced.audio',
      outcome: 'verified',
      note: 'On the form.',
    };

    expect(sourceEntryResult('songs.advanced.audio', form(), verified)).toEqual(verified);
  });
});

describe('opening the source clip’s menu and choosing the action (TS-003 menus)', () => {
  it('opens the Remix menu and chooses Cover, pressing nothing else', async () => {
    const page = loadSnapshot('clip-remix-menu', SONG_PAGE);
    const pressed = recordPresses();

    expect((await run(page, openSourceMenu, { route: SOURCE_ROUTES.cover })).ok).toBe(true);
    expect((await run(page, chooseSourceAction, { route: SOURCE_ROUTES.cover })).ok).toBe(true);

    expect(pressed).toEqual(['Cover']);
  });

  it('opens the Remix menu when only the Edit menu is open, and finds Edit › Extend in the Edit menu', async () => {
    const remix = loadSnapshot('clip-edit-menu', SONG_PAGE);
    const pressed = recordPresses();
    // The Remix item is closed in this snapshot; pressing it opens nothing here, so the step stops.
    const result = await run(remix, openSourceMenu, { route: SOURCE_ROUTES.cover });
    expect(result.ok ? null : result.failure.step).toBe('action menu');
    expect(pressed).toEqual(['Remix']);

    const edit = loadSnapshot('clip-edit-menu', SONG_PAGE);
    expect((await run(edit, openSourceMenu, { route: SOURCE_ROUTES.extend })).ok).toBe(true);
  });

  it('reports a disabled or missing action as unavailable at its step, choosing nothing', async () => {
    for (const change of [
      (item: Element) => {
        item.setAttribute('aria-disabled', 'true');
      },
      (item: Element) => {
        item.remove();
      },
    ]) {
      const page = loadSnapshot('clip-remix-menu', SONG_PAGE);
      const item = document.querySelector('[role="menuitem"][aria-label="Cover"]');
      if (item === null) {
        throw new Error('The snapshot has no Cover item.');
      }
      change(item);
      const pressed = recordPresses();

      const result = await run(page, openSourceMenu, { route: SOURCE_ROUTES.cover });

      expect(result.ok ? null : result.failure.step).toBe('action offered');
      expect(pressed).toEqual([]);
    }
  });

  it('stops at the clip menu on a page that is not a clip’s, pressing none of its More options', async () => {
    // The Library has one per row and no clip header: none is the clip's own, so none is pressed.
    const page = loadSnapshot('library-list', 'https://suno.com/me');
    const pressed = recordPresses();

    const result = await run(page, openSourceMenu, { route: SOURCE_ROUTES.cover });

    expect(result.ok ? null : result.failure).toMatchObject({
      step: 'clip menu',
      expected: 'the clip’s cover image at the top of its page',
    });
    expect(pressed).toEqual([]);
  });

  it('knows a route for every action key a source can carry, and Use as Inspiration', () => {
    const routes: SourceRoute[] = [...Object.values(SOURCE_ROUTES), INSPIRATION_ROUTE];
    expect(Object.keys(SOURCE_ROUTES).sort()).toEqual([
      'cover',
      'extend',
      'mashup',
      'reuse_prompt',
      'sample',
    ]);
    // Every item is in the captured menus, under the submenu the route names.
    for (const route of routes) {
      const snapshot = route.menu === 'Remix' ? 'clip-remix-menu' : 'clip-edit-menu';
      loadSnapshot(snapshot, SONG_PAGE);
      expect(
        document.querySelector(`[role="menuitem"][aria-label="${route.item}"]`),
        route.item,
      ).not.toBeNull();
    }
  });
});

describe('the clip’s own page (#148, TS-005)', () => {
  it('presses the header’s More options, of the twelve on the page, then Remix › Cover', async () => {
    const page = loadSnapshot('clip-page', `https://suno.com/song/${CLIP_PAGE_CLIP}`);
    expect(document.querySelectorAll('button[aria-label="More options"]').length).toBe(2);
    const pressed = recordPresses();
    // Suno opens the menu (and the Remix submenu, as the capture shows it).
    const header = document.querySelector('img[alt="Song Cover Image"]');
    document.addEventListener(
      'click',
      (event) => {
        const button = (event.target as Element).closest('button[aria-label="More options"]');
        if (
          button !== null &&
          header?.parentElement?.parentElement?.parentElement?.contains(button)
        ) {
          document.body.innerHTML = snapshotHtml('clip-page-remix-menu');
        }
      },
      { once: true },
    );

    expect((await run(page, openSourceMenu, { route: SOURCE_ROUTES.cover })).ok).toBe(true);
    expect((await run(page, chooseSourceAction, { route: SOURCE_ROUTES.cover })).ok).toBe(true);

    expect(pressed).toEqual(['More options', 'Cover']);
  });

  it('chooses each Remix action from the clip page’s menu, never reaching Publish or Move to Trash', async () => {
    const remix: SourceRoute[] = [
      ...Object.values(SOURCE_ROUTES).filter((route) => route.menu === 'Remix'),
      INSPIRATION_ROUTE,
    ];
    expect(remix).toHaveLength(5);
    for (const route of remix) {
      const page = loadSnapshot('clip-page-remix-menu', `https://suno.com/song/${CLIP_PAGE_CLIP}`);
      const pressed = recordPresses();

      expect((await run(page, openSourceMenu, { route })).ok, route.item).toBe(true);
      expect((await run(page, chooseSourceAction, { route })).ok, route.item).toBe(true);

      expect(pressed).toEqual([route.item]);
      document.body.innerHTML = '';
    }
  });

  it('tells the clip’s page from another clip’s, from Suno’s 404, and from nothing yet', () => {
    const page = loadSnapshot('clip-page', `https://suno.com/song/${CLIP_PAGE_CLIP}`);
    expect(clipPageShows(page, CLIP_PAGE_CLIP)).toBe('clip');
    expect(clipPageShows(page, OTHER_CLIP)).toBe('another clip');

    loadSnapshot('clip-not-found', `https://suno.com/song/${OTHER_CLIP}`);
    expect(clipPageShows(page, OTHER_CLIP)).toBe('not found');

    document.body.innerHTML = '';
    expect(clipPageShows(page, CLIP_PAGE_CLIP)).toBe('nothing yet');
  });
});

describe('the Advanced form after Extend, Sample, Mashup, and Inspiration (#148, TS-005)', () => {
  const advanced = (sources: FormSource[]): LoadedSource => loaded(form({ sources }));
  const inspiration = (sunoId: string, position = 1): FormSource =>
    source({
      key: 'songs.advanced.inspiration',
      group: 'inspiration',
      sunoAction: null,
      sunoId,
      position,
    });

  it.each([
    ['create-source-extend', 'extend'],
    ['create-source-sample', 'sample'],
    ['create-source-mashup-one-song', 'mashup'],
  ])(
    'verifies %s by its action and thumbnail, and refuses another clip',
    async (snapshot, action) => {
      const page = loadSnapshot(snapshot);

      const right = await run(page, verifySourceAdvanced, {
        load: advanced([source({ sunoAction: action, sunoId: LOADED_SOURCE })]),
      });
      const wrong = await run(page, verifySourceAdvanced, {
        load: advanced([source({ sunoAction: action, sunoId: OTHER_CLIP })]),
      });
      const otherAction = await run(page, verifySourceAdvanced, {
        load: advanced([source({ sunoAction: 'cover', sunoId: LOADED_SOURCE })]),
      });

      expect(right.ok).toBe(true);
      expect(wrong.ok ? null : wrong.failure.step).toBe('source shown');
      expect(otherAction.ok ? null : otherAction.failure.expected).toBe(
        'the Audio section to name the source action of the Version',
      );
    },
  );

  it('does not verify a Mashup with one of its two songs: the second is added by hand first', async () => {
    const page = loadSnapshot('create-source-mashup-one-song');

    const result = await run(page, verifySourceAdvanced, {
      load: advanced([
        source({ sunoAction: 'mashup', sunoId: LOADED_SOURCE }),
        source({ sunoAction: 'mashup', sunoId: OTHER_CLIP, position: 2 }),
      ]),
    });

    expect(result.ok ? null : result.failure.expected).toBe(
      'the Mashup to show both source clips of the Version, and no other',
    );
  });

  it('verifies one Inspiration song, and not two of which one is shown', async () => {
    const page = loadSnapshot('create-source-inspo-one-song');

    const one = await run(page, verifySourceAdvanced, {
      load: advanced([inspiration(LOADED_SOURCE)]),
    });
    const two = await run(page, verifySourceAdvanced, {
      load: advanced([inspiration(LOADED_SOURCE), inspiration(OTHER_CLIP, 2)]),
    });

    expect(one.ok).toBe(true);
    expect(two.ok ? null : two.failure.expected).toBe(
      'the Audio section to show every Inspiration song of the Version, and no other',
    );
  });

  it('sets the Extend’s “Extend from” time and reads it back', async () => {
    const page = loadSnapshot('create-source-extend');
    const standIn = standInForSuno(document);
    try {
      expect((await run(page, setExtendFrom, { seconds: 42 })).ok).toBe(true);
    } finally {
      standIn.stop();
    }

    expect(standIn.commands).toEqual(['insertText']);
    expect(document.querySelector('[contenteditable="true"]')?.textContent).toBe('00:42.0');
  });

  it('stops at its step when the time does not take, and changes nothing when it already shows it', async () => {
    const page = loadSnapshot('create-source-extend');
    Object.defineProperty(document, 'execCommand', { configurable: true, value: () => false });
    try {
      const result = await run(page, setExtendFrom, { seconds: 42 });
      expect(result.ok ? null : result.failure).toMatchObject({
        step: 'extend from',
        expected: 'the “Extend from” time to show where the Version continues from',
      });
    } finally {
      Reflect.deleteProperty(document, 'execCommand');
    }
    // The snapshot's own time: nothing is typed.
    expect((await run(page, setExtendFrom, { seconds: 54 })).ok).toBe(true);
  });

  it('writes and reads Suno’s times', () => {
    expect(extendTime(54)).toBe('00:54.0');
    expect(extendTime(125.25)).toBe('02:05.3');
    expect(extendSeconds('01:00.0')).toBe(60);
    expect(extendSeconds(' 00:54.0 ')).toBe(54);
    expect(extendSeconds('soon')).toBeNull();
  });
});

describe('choosing the Voice (#148, TS-005)', () => {
  const voice = { name: VOICE_NAME, personaId: VOICE_PERSONA };

  it('opens + Voice, presses the voice’s title, and verifies it by the chosen voice’s link', async () => {
    const page = loadSnapshot('create-source-inspo-one-song');
    const pressed: string[] = [];
    document.addEventListener(
      'click',
      (event) => {
        const target = event.target as Element;
        pressed.push(target.closest('button')?.getAttribute('aria-label') ?? target.textContent);
        if (target.closest('button')?.getAttribute('aria-label') === 'Add Voice') {
          document.body.innerHTML = snapshotHtml('voice-picker-with-source');
        } else if (target.textContent === VOICE_NAME) {
          // Suno chooses it and closes the picker (TS-005).
          document.body.innerHTML = snapshotHtml('create-voice-selected');
        }
      },
      { capture: true },
    );

    const result = await run(page, chooseVoice, { voice });

    expect(result.ok).toBe(true);
    expect(pressed).toEqual(['Add Voice', VOICE_NAME]);
  });

  it('leaves a voice already chosen alone, and refuses another voice’s link', async () => {
    const page = loadSnapshot('create-voice-selected');
    const pressed = recordPresses();

    expect((await run(page, chooseVoice, { voice })).ok).toBe(true);
    expect(pressed).toEqual([]);

    const other = await run(page, chooseVoice, {
      voice: { name: 'Somebody', personaId: OTHER_CLIP },
    });
    expect(other.ok).toBe(false);
  });

  it('chooses no voice when two in the picker have its name, and never presses “Create Voice”', async () => {
    const page = loadSnapshot('voice-picker-with-source');
    const titles = [...document.querySelectorAll('[role="dialog"] span')].filter(
      (span) => span.children.length === 0 && span.textContent === '<redacted 21 chars>',
    );
    titles[0]?.parentElement?.append(titles[0].cloneNode(true));
    const pressed = recordPresses();

    const twice = await run(page, chooseVoice, {
      voice: { name: '<redacted 21 chars>', personaId: VOICE_PERSONA },
    });
    expect(twice.ok ? null : twice.failure.step).toBe('voice');
    expect(twice.ok ? null : twice.failure.expected).toContain('found 2');

    const create = await run(page, chooseVoice, {
      voice: { name: 'Create Voice', personaId: VOICE_PERSONA },
    });
    expect(create.ok ? null : create.failure.kind).toBe('refused');
    expect(pressed).toEqual([]);
  });
});

describe('Suno’s Overwrite question (TS-003)', () => {
  it.each(['overwrite-lyrics-styles-dialog', 'overwrite-styles-dialog'])(
    'answers Overwrite in %s, and nothing else',
    async (snapshot) => {
      const page = loadSnapshot(snapshot);
      const pressed = recordPresses();

      expect((await run(page, answerOverwrite, {})).ok).toBe(true);

      expect(pressed).toEqual(['Overwrite']);
    },
  );
});

describe('verifying the source on the Create form (TS-002)', () => {
  it('verifies a Cover in the Advanced Audio section: the action and the thumbnail’s clip ID', async () => {
    const page = loadSnapshot('create-source-advanced');

    expect((await run(page, verifySourceAdvanced, { load: loaded(form()) })).ok).toBe(true);
  });

  it('stops naming the step when the thumbnail holds another clip’s ID, or the action differs', async () => {
    const page = loadSnapshot('create-source-advanced');

    const wrongClip = await run(page, verifySourceAdvanced, {
      load: loaded(form({ sources: [source({ sunoId: OTHER_CLIP })] })),
    });
    expect(wrongClip.ok ? null : wrongClip.failure).toMatchObject({
      step: 'source shown',
      expected: 'the Audio section to show the source clip of the Version',
      pageMayBeChanged: false,
    });

    const otherAction: LoadedSource = {
      ...loaded(form()),
      route: { ...SOURCE_ROUTES.cover, item: 'Mashup', label: 'Mashup' } as SourceRoute,
    };
    const wrongAction = await run(page, verifySourceAdvanced, { load: otherAction });
    expect(wrongAction.ok ? null : wrongAction.failure.expected).toBe(
      'the Audio section to name the source action of the Version',
    );
  });

  // #343: a title with curly and straight quotes split the report's quote redaction. No value of
  // the Version goes into what a step expected, so nothing of it can reach the diagnostic report.
  it.each(['advanced', 'simple'])(
    'puts nothing of the source’s title, action, or ID into what was expected (%s)',
    async (mode) => {
      const title = 'Song “quoted” words "said" ”midnight whisper“ tail';
      const page = loadSnapshot(
        mode === 'simple' ? 'create-source-simple' : 'create-source-advanced',
      );
      const load = loaded(
        form({
          mode,
          sources: [
            source({
              key: `songs.${mode}.audio`,
              title,
              sunoId: OTHER_CLIP,
            }),
          ],
        }),
      );

      const result = await run(
        page,
        mode === 'simple' ? verifySourceSimple : verifySourceAdvanced,
        {
          load,
        },
      );
      const failure = result.ok ? null : result.failure;
      const report = stepsOfRun(
        { workflowId: failure?.workflowId ?? '', log: result.log, failure, structure: null },
        ADAPTER_WORKFLOWS,
      );

      expect(failure?.step).toBe('source shown');
      const written = JSON.stringify([failure?.expected, report]);
      for (const word of [
        'Song',
        'quoted',
        'words',
        'said',
        'midnight',
        'whisper',
        'tail',
        OTHER_CLIP,
      ]) {
        expect(written).not.toContain(word);
      }
      expect(report.at(-1)?.expected).toBe(
        mode === 'simple'
          ? 'the source chip to show the source clip of the Version'
          : 'the Audio section to show the source clip of the Version',
      );
    },
  );

  it('verifies a Cover on the Simple chip by its thumbnail, and refuses another clip', async () => {
    const page = loadSnapshot('create-source-simple');
    const simple = (sunoId: string) =>
      loaded(form({ mode: 'simple', sources: [source({ key: 'songs.simple.audio', sunoId })] }));

    expect((await run(page, verifySourceSimple, { load: simple(SIMPLE_SOURCE) })).ok).toBe(true);
    const wrong = await run(page, verifySourceSimple, { load: simple(ADVANCED_SOURCE) });
    expect(wrong.ok ? null : wrong.failure.step).toBe('source shown');
  });

  it('finds no source on a form that has none', async () => {
    const page = loadSnapshot('create-songs-advanced-more-options');

    const result = await run(page, verifySourceAdvanced, { load: loaded(form()) });

    expect(result.ok ? null : result.failure.expected).toContain('condition button');
  });

  it('matches a clip ID inside the thumbnail’s path only', () => {
    expect(holdsClip(`https://cdn2.suno.ai/image_${ADVANCED_SOURCE}.jpeg`, ADVANCED_SOURCE)).toBe(
      true,
    );
    expect(holdsClip(`https://cdn2.suno.ai/x.jpeg?id=${ADVANCED_SOURCE}`, ADVANCED_SOURCE)).toBe(
      false,
    );
    expect(holdsClip(null, ADVANCED_SOURCE)).toBe(false);
    expect(holdsClip('not an address', ADVANCED_SOURCE)).toBe(false);
  });
});

/**
 * What the coverage test requires (AC 9): every `source` entry of the Songs field map has a
 * workflow and fixture tests of success, wrong source, and unavailable source, or is listed as
 * blocked on a capture (D10) with the reason the summary gives. The audio entries are covered by
 * Cover and Reuse Prompt (above); Extend, Mashup, Sample this song, and a single Inspiration song
 * are by hand until captured.
 */
const COVERED: Readonly<Record<string, readonly string[]>> = {
  'songs.simple.audio': ['verify-source-simple', 'open-source-menu', 'choose-source-action'],
  'songs.advanced.audio': [
    'verify-source-advanced',
    'open-source-menu',
    'choose-source-action',
    'set-extend-from',
  ],
  'songs.advanced.voice': ['choose-voice'],
};

function sourceCoverageProblems(
  covered: Readonly<Record<string, readonly string[]>>,
  blocked: Readonly<Record<string, string>>,
): string[] {
  const problems: string[] = [];
  for (const { entry, how } of FIELD_MAP) {
    if (!entry.startsWith('songs.') || how !== 'source') {
      continue;
    }
    const workflows = covered[entry];
    if (workflows !== undefined && blocked[entry] !== undefined) {
      problems.push(`${entry}: is covered but listed as blocked on a capture`);
    } else if (workflows === undefined && blocked[entry] === undefined) {
      problems.push(`${entry}: has no source workflow and is not listed as blocked on a capture`);
    }
  }
  return problems;
}

describe('the coverage of the source entries (AC 9)', () => {
  it('has a workflow with success, wrong-source, and unavailable tests, or a capture to wait for, for every source entry', () => {
    expect(sourceCoverageProblems(COVERED, SOURCES_BLOCKED_ON_CAPTURE)).toEqual([]);
  });

  it('bites: with an entry neither covered nor blocked, the coverage test names it', () => {
    const blocked = Object.fromEntries(
      Object.entries(SOURCES_BLOCKED_ON_CAPTURE).filter(
        ([entry]) => entry !== 'songs.simple.voice',
      ),
    );

    expect(sourceCoverageProblems(COVERED, blocked)).toEqual([
      'songs.simple.voice: has no source workflow and is not listed as blocked on a capture',
    ]);
  });

  it('lists exactly the source entries no snapshot shows the result of (D10): the playlist, and Simple’s voice', () => {
    expect(Object.keys(SOURCES_BLOCKED_ON_CAPTURE).sort()).toEqual([
      'songs.advanced.inspiration',
      'songs.simple.simple_add_playlist',
      'songs.simple.voice',
    ]);
    // Every action's Advanced form is captured (TS-003, TS-005).
    expect(
      Object.entries(SOURCE_ROUTES)
        .filter(([, route]) => !route.captured)
        .map(([action]) => action),
    ).toEqual([]);
  });
});
