// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { stepsOfRun } from '../diagnostics/report.ts';
import { fakeClock, loadSnapshot, SNAPSHOT_NAMES } from '../testing/snapshots.ts';
import { FIELD_MAP } from './fieldMap.ts';
import type { EntryResult, FormJob, FormSource } from './fill.ts';
import { nameOf, type Page } from './primitives.ts';
import {
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
  openSourceMenu,
  verifySourceAdvanced,
  verifySourceSimple,
} from './workflows/sources.ts';
import { ADAPTER_WORKFLOWS } from './workflows/index.ts';

/** The source clips of the TS-003 snapshots: the Advanced Audio section's, and the Simple chip's. */
const ADVANCED_SOURCE = '00000000-0000-4000-8000-000000000104';
const SIMPLE_SOURCE = '00000000-0000-4000-8000-000000000109';
const OTHER_CLIP = '00000000-0000-4000-8000-000000000199';

const SONG_PAGE = `https://suno.com/song/${ADVANCED_SOURCE}`;

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
      load: { source: source(), sunoId: ADVANCED_SOURCE, route: SOURCE_ROUTES.cover },
    });
    expect(
      planSources(form({ sources: [source({ sunoAction: 'reuse_prompt' })] })).load?.route,
    ).toEqual({
      menu: 'Remix',
      item: 'Reuse Prompt',
      label: null,
      captured: true,
      automated: false,
    });
  });

  // #341: the clip's page and its More options button are not captured, so no route is taken.
  it('takes no route itself while the clip’s page is not captured: every source is loaded by hand', () => {
    expect(
      [...Object.values(SOURCE_ROUTES), INSPIRATION_ROUTE].filter((route) => route.automated),
    ).toEqual([]);
    expect(SNAPSHOT_NAMES.some((name) => name.startsWith('clip-page'))).toBe(false);
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

  it('loads nothing it cannot verify: Extend, Sample, a Mashup, a Song-level source, a general Remix', () => {
    for (const sources of [
      [source({ sunoAction: 'extend', continueAtSeconds: 42 })],
      [source({ sunoAction: 'sample' })],
      [source({ sunoAction: 'mashup' }), source({ sunoAction: 'mashup', position: 2 })],
      [source({ sunoId: null })],
      [source({ sunoAction: null })],
    ]) {
      expect(planSources(form({ sources }))).toEqual({ stop: null, load: null });
    }
  });
});

describe('the summary lines of the source entries (#148)', () => {
  it('names what is to do by hand, and why, for every source the extension does not load', () => {
    const job = form({
      sources: [
        source({ sunoAction: 'extend', continueAtSeconds: 42 }),
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
      note: 'Load “Night Drive (demo)” with Edit › Extend by hand: no snapshot shows Suno’s form after Extend yet.',
      reportNote:
        'Load the source with Edit › Extend by hand: no snapshot shows Suno’s form after Extend yet.',
    });
    expect(sourceEntryResult('songs.advanced.inspiration', job)).toMatchObject({
      outcome: 'manual',
      note: 'Add “Rain” as Inspiration (Use as Inspiration) by hand: no snapshot shows that form yet.',
    });
    // Voice is to do by hand with the voice's name, never failed.
    expect(sourceEntryResult('songs.advanced.voice', job)).toEqual({
      key: 'songs.advanced.voice',
      outcome: 'manual',
      note: 'Choose the voice “Velvet” from + Voice by hand (no snapshot shows the form with a voice chosen).',
      reportNote:
        'Choose the voice the Version names from + Voice by hand (no snapshot shows the form with a voice chosen).',
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

  it('stops at the clip menu when the page has no single More options button', async () => {
    // The Library has one per row: the clip's own is not told apart, so none is pressed.
    const page = loadSnapshot('library-list', 'https://suno.com/me');
    const pressed = recordPresses();

    const result = await run(page, openSourceMenu, { route: SOURCE_ROUTES.cover });

    expect(result.ok ? null : result.failure).toMatchObject({
      step: 'clip menu',
      expected: expect.stringContaining('More options') as string,
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

describe('Suno’s Overwrite question (TS-003)', () => {
  it('answers Overwrite, and nothing else', async () => {
    const page = loadSnapshot('overwrite-lyrics-styles-dialog');
    const pressed = recordPresses();

    expect((await run(page, answerOverwrite, {})).ok).toBe(true);

    expect(pressed).toEqual(['Overwrite']);
  });
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
  'songs.advanced.audio': ['verify-source-advanced', 'open-source-menu', 'choose-source-action'],
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
        ([entry]) => entry !== 'songs.advanced.voice',
      ),
    );

    expect(sourceCoverageProblems(COVERED, blocked)).toEqual([
      'songs.advanced.voice: has no source workflow and is not listed as blocked on a capture',
    ]);
  });

  it('lists exactly the source entries no TS-003 snapshot shows the result of (D10)', () => {
    expect(Object.keys(SOURCES_BLOCKED_ON_CAPTURE).sort()).toEqual([
      'songs.advanced.inspiration',
      'songs.advanced.voice',
      'songs.simple.simple_add_playlist',
      'songs.simple.voice',
    ]);
    expect(
      Object.entries(SOURCE_ROUTES)
        .filter(([, route]) => !route.captured)
        .map(([action]) => action)
        .sort(),
    ).toEqual(['extend', 'mashup', 'sample']);
  });
});
