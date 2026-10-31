// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { SNAPSHOT_NAMES, snapshotHtml } from '../testing/snapshots.ts';
import {
  classify,
  CREATE_WORKSPACE,
  DOWNLOAD_CLIP,
  EXCEPTIONS,
  MASHUP_PICKER,
  RECOGNISED_DIALOGS,
  REPLACE_LYRICS,
  SAVE_TO_PICKER,
  type ControlFacts,
  type Verdict,
} from './forbidden.ts';
import { nameOf, roleOf, verdictOf } from './primitives.ts';

function load(snapshot: string): void {
  document.body.innerHTML = snapshotHtml(snapshot);
}

/** The elements on the loaded page with this role and accessible name. */
function controls(role: string, name: string | RegExp): Element[] {
  return [...document.body.querySelectorAll('*')].filter(
    (element) =>
      roleOf(element) === role &&
      (typeof name === 'string' ? nameOf(element) === name : name.test(nameOf(element))),
  );
}

function verdictsOf(role: string, name: string | RegExp): Verdict['kind'][] {
  const found = controls(role, name);
  expect(found.length, `${role} "${String(name)}" on the page`).toBeGreaterThan(0);
  return [...new Set(found.map((element) => verdictOf(element).kind))];
}

function facts(change: Partial<ControlFacts>): ControlFacts {
  return {
    role: 'button',
    name: 'Play',
    otherNames: [],
    matches: () => false,
    dialog: null,
    inlineField: () => null,
    fieldOnPage: () => false,
    submitsForm: false,
    ...change,
  };
}

afterEach(() => {
  document.body.innerHTML = '';
});

describe('the forbidden-control matcher, on the TS-003 snapshots', () => {
  it.each([
    ['create-songs-simple', 'Create song'],
    ['create-songs-advanced-more-options', 'Create song'],
    ['create-speech-simple', 'Create speech'],
    ['create-speech-advanced', 'Create speech'],
    ['create-sounds-advanced-options', 'Create song'],
  ])('recognises the Create button in %s', (snapshot, name) => {
    load(snapshot);
    expect(verdictsOf('button', name)).toEqual(['forbidden']);
  });

  it.each([
    ['clip-remix-menu', 'menuitem', 'Publish'],
    ['clip-edit-menu', 'menuitem', 'Publish'],
    ['clip-download-menu', 'menuitem', 'Publish'],
    ['clip-remix-menu', 'menuitem', 'Move to Trash'],
    ['clip-edit-menu', 'menuitem', 'Move to Trash'],
    ['clip-download-menu', 'menuitem', 'Move to Trash'],
    ['clip-edit-menu', 'menuitem', 'Remove Section'],
    ['library-list', 'button', 'Publish clip'],
    ['library-list', 'button', 'Trash'],
    ['library-trash', 'button', 'Delete permanently'],
    ['create-source-simple', 'button', /^Remove /],
    ['download-dialog', 'button', 'MP4 video asset'],
    ['download-dialog', 'button', 'Manage'],
    ['inspo-picker', 'button', 'Close'],
    ['overwrite-lyrics-styles-dialog', 'button', 'Close'],
  ])('recognises in %s the %s %s', (snapshot, role, name) => {
    load(snapshot);
    expect(verdictsOf(role, name)).toEqual(['forbidden']);
  });

  it.each([
    ['create-songs-advanced-more-options', 'tab', 'Songs'],
    ['create-songs-advanced-more-options', 'tab', 'Speech'],
    ['create-songs-advanced-more-options', 'tab', 'Sounds'],
    ['create-songs-advanced-more-options', 'tab', 'Simple'],
    ['create-songs-advanced-more-options', 'tab', 'Advanced'],
    ['create-songs-advanced-more-options', 'button', 'Clear all form inputs'],
    ['create-songs-advanced-more-options', 'button', 'Add Voice'],
    ['workspace-selector', 'button', /^My Workspace /],
    ['workspace-selector', 'button', 'View Archived'],
    ['workspace-selector', 'link', 'Create'],
    ['workspace-selector', 'link', 'Library'],
    ['library-trash', 'button', 'Restore to library'],
    ['clip-remix-menu', 'menuitem', 'Cover'],
    ['overwrite-lyrics-styles-dialog', 'button', 'Overwrite'],
    ['overwrite-lyrics-styles-dialog', 'button', 'Keep Current'],
    ['voice-picker', 'button', 'Close'],
    ['voice-picker', 'button', 'My Voices'],
    ['voice-picker', 'button', 'Favorites'],
  ])('leaves alone in %s the %s %s', (snapshot, role, name) => {
    load(snapshot);
    expect(verdictsOf(role, name)).toEqual(['allowed']);
  });

  it("recognises the create-workspace dialog's Confirm as the named exception, not as forbidden", () => {
    load('create-workspace-dialog');
    expect(verdictsOf('button', 'Confirm')).toEqual(['exception']);
    const [confirm] = controls('button', 'Confirm');
    expect(confirm && verdictOf(confirm)).toEqual({
      kind: 'exception',
      exception: 'create-workspace',
    });
  });

  it('recognises the "Create new workspace" entry as the same exception', () => {
    load('workspace-selector');
    expect(verdictsOf('button', 'Create new workspace')).toEqual(['exception']);
  });

  it.each(['WAV', 'MP3', 'M4A', 'Unlock & Download', 'Close'])(
    "recognises the Download dialog's %s as the download exception, pressed only by its primitive (#216)",
    (name) => {
      load('download-dialog');
      const [control] = controls('button', name);
      expect(control && verdictOf(control)).toEqual({
        kind: 'exception',
        exception: 'download-clip',
      });
    },
  );

  it('names exactly two permitted changes, creating a workspace and preparing a download, and finds them nowhere else', () => {
    expect(EXCEPTIONS.map((exception) => exception.name)).toEqual([
      'create-workspace',
      'download-clip',
    ]);
    expect(CREATE_WORKSPACE.snapshots).toEqual(['workspace-selector', 'create-workspace-dialog']);

    const found: string[] = [];
    for (const snapshot of SNAPSHOT_NAMES) {
      load(snapshot);
      for (const element of document.body.querySelectorAll('*')) {
        if (verdictOf(element).kind === 'exception') {
          found.push(`${snapshot}: ${nameOf(element)}`);
        }
      }
    }
    expect(found).toEqual([
      'create-workspace-dialog: Confirm',
      'download-dialog: Close',
      'download-dialog: M4A',
      'download-dialog: MP3',
      'download-dialog: WAV',
      'download-dialog: Unlock & Download',
      // TS-007-23: Library's Workspaces tab holds the same entry.
      'library-workspaces: Create new workspace',
      'workspace-selector: Create new workspace',
    ]);
    expect(DOWNLOAD_CLIP.snapshots).toEqual(['download-dialog']);
    // The matcher's verdict on every element of every snapshot reads jsdom's computed styles: about
    // 2.5 s here and over 5 s on a GitHub runner, so the limit is three times a CI run's ~10 s.
  }, 30_000);

  it('recognises only the two Overwrite questions, the Voice picker, Simple’s Lyrics and Styles dialogs, the Key popover, the Model dialog, the Mashup dialog, and its Replace Lyrics question', () => {
    expect(RECOGNISED_DIALOGS.map((dialog) => [dialog.title, dialog.allows])).toEqual([
      ['Overwrite Lyrics & Styles?', ['Overwrite', 'Keep Current']],
      ['Overwrite Styles?', ['Overwrite', 'Keep Current']],
      ['Voice', ['Close', 'My Voices', 'Favorites']],
      ['Lyrics', ['Close']],
      ['Styles', ['Close']],
      [
        '',
        [
          'C',
          'C#',
          'D',
          'D#',
          'E',
          'F',
          'F#',
          'G',
          'G#',
          'A',
          'A#',
          'B',
          'Any',
          'Major',
          'Minor',
          'Apply',
        ],
      ],
      ['Model', ['Close']],
      ['Add a song to mashup', ['Close', 'Search clips']],
      ['Replace Lyrics?', ['Keep Current Lyrics', 'Close']],
    ]);
    // Only the Mashup dialog allows buttons by a pattern: a song's "Add <title> to mashup" (#438).
    expect(
      RECOGNISED_DIALOGS.filter((dialog) => dialog.allowsButtons !== undefined).map((dialog) => [
        dialog.title,
        String(dialog.allowsButtons),
      ]),
    ).toEqual([['Add a song to mashup', String(/^Add .+ to mashup$/)]]);
    // Only the Model dialog's rows may be pressed (TS-007-10).
    expect(
      RECOGNISED_DIALOGS.filter((dialog) => dialog.pressesRows === true).map(
        (dialog) => dialog.title,
      ),
    ).toEqual(['Model']);
    // Each was recognised from a snapshot of it.
    for (const dialog of RECOGNISED_DIALOGS) {
      expect(SNAPSHOT_NAMES).toContain(dialog.snapshot);
    }
  });

  it('still refuses everything else in the Voice picker', () => {
    load('voice-picker');
    const play = verdictsOf('button', 'Play');
    expect(play.length).toBeGreaterThan(0);
    expect(new Set(play)).toEqual(new Set(['forbidden']));
  });
});

describe('the dialogs TS-005 captured (#146, #147, #148)', () => {
  it.each([
    ['overwrite-styles-dialog', 'Overwrite', 'allowed'],
    ['overwrite-styles-dialog', 'Keep Current', 'allowed'],
    ['overwrite-styles-dialog', 'Close', 'forbidden'],
    ['create-songs-simple-styles-dialog', 'Close', 'allowed'],
    ['create-songs-simple-styles-dialog', 'Save prompt', 'forbidden'],
    ['create-songs-simple-styles-dialog', 'Clear styles', 'forbidden'],
    ['create-songs-simple-lyrics-dialog', 'New draft', 'forbidden'],
    ['create-songs-simple-lyrics-dialog', 'Generate', 'forbidden'],
    ['create-songs-simple-lyrics-dialog', 'Help me write lyrics', 'forbidden'],
    ['create-sounds-key-popover-fsharp-minor', 'F#', 'allowed'],
    ['create-sounds-key-popover-fsharp-minor', 'Any', 'allowed'],
    ['create-sounds-key-popover-fsharp-minor', 'Apply', 'allowed'],
    ['voice-picker-with-source', 'My Voices', 'allowed'],
    ['voice-picker-with-source', 'Play', 'forbidden'],
  ])('in %s, %s is %s', (snapshot, name, verdict) => {
    load(snapshot);
    const found = [...document.querySelectorAll('[role="dialog"] *')].filter(
      (element) => roleOf(element) !== null && nameOf(element) === name,
    );
    expect(found.length).toBeGreaterThan(0);
    expect([...new Set(found.map((element) => verdictOf(element).kind))]).toEqual([verdict]);
  });

  it('recognises the untitled Key popover only while it holds nothing but its notes, Any, Major, Minor, and Apply', () => {
    load('create-sounds-key-popover-fsharp-minor');
    const popover = document.querySelector('[role="dialog"]');
    const apply = [...(popover?.querySelectorAll('button') ?? [])].find(
      (button) => button.textContent.trim() === 'Apply',
    );
    expect(apply && verdictOf(apply)).toEqual({ kind: 'allowed' });

    // Complement: the same popover with one more control is any other untitled dialog.
    const extra = document.createElement('button');
    extra.textContent = 'Share';
    popover?.append(extra);
    expect(apply && verdictOf(apply)).toEqual({
      kind: 'forbidden',
      reason: 'it is in a dialog the adapter does not recognise',
    });
    expect(classify(facts({ dialog: { title: '' }, name: 'Apply' })).kind).toBe('forbidden');
  });

  it('takes a tab list in a recognised dialog as a container of its tabs, and fails it closed elsewhere', () => {
    load('create-sounds-key-popover-fsharp-minor');
    const tablist = document.querySelector('[role="dialog"] [role="tablist"]');
    expect(tablist && verdictOf(tablist)).toEqual({ kind: 'allowed' });
    expect(classify(facts({ role: 'tablist', name: '', dialog: { title: 'Rename' } })).kind).toBe(
      'forbidden',
    );
  });

  it('lets a voice be chosen in the Voice picker by its title, and refuses “Create Voice”', () => {
    load('voice-picker-with-source');
    const texts = [...document.querySelectorAll('[role="dialog"] span')].filter(
      (span) => span.children.length === 0 && roleOf(span) === null,
    );
    // The voice's title, redacted in the snapshot.
    const title = texts.find((span) => span.textContent === '<redacted 13 chars>');
    const create = texts.find((span) => span.textContent === 'Create Voice');
    expect(title && verdictOf(title)).toEqual({ kind: 'allowed' });
    expect(create && verdictOf(create)).toEqual({
      kind: 'forbidden',
      reason: 'the dialog it is in does not allow it',
    });
    // Complement: a text with no role is pressable in the Voice picker only.
    expect(
      classify(
        facts({ role: null, name: '', otherNames: ['A voice'], dialog: { title: 'Styles' } }),
      ).kind,
    ).toBe('forbidden');
  });
});

describe('the Model dialog (#427, TS-007-10)', () => {
  /** The Model dialog's rows, by their first text. */
  function row(lead: string): Element {
    const found = [...document.querySelectorAll('[role="dialog"] [role="row"]')].find(
      (element) => element.querySelector('span')?.textContent.trim() === lead,
    );
    if (found === undefined) {
      throw new Error(`no row ${lead}`);
    }
    return found;
  }

  it('lets a model’s row be pressed, and refuses the Create Custom Model row, which spends credits', () => {
    load('create-songs-advanced-model-dialog');

    for (const model of ['v6', 'v6-wild', 'v6-mini']) {
      expect(verdictOf(row(model)), model).toEqual({ kind: 'allowed' });
    }
    expect(verdictOf(row('Create Custom Model'))).toEqual({
      kind: 'forbidden',
      reason: 'its name starts with Create, Publish, Delete, Trash, or Remove',
    });
    // Nothing inside it may be pressed either: the click would reach the row.
    const inside = row('Create Custom Model').querySelector('span');
    expect(inside && verdictOf(inside).kind).toBe('forbidden');
  });

  it('refuses a row whose text starts with a forbidden word, in the Model dialog only by that rule', () => {
    const model = { title: 'Model' };
    expect(
      classify(facts({ role: 'row', name: '', otherNames: ['v6-wild Pro'], dialog: model })),
    ).toEqual({ kind: 'allowed' });
    for (const text of ['Create Custom Model Beta', 'Delete model', 'Remove v6']) {
      expect(
        classify(facts({ role: 'row', name: '', otherNames: [text], dialog: model })).kind,
        text,
      ).toBe('forbidden');
    }
    // Complement: a row in another recognised dialog is not one of its controls.
    expect(
      classify(facts({ role: 'row', name: '', otherNames: ['v6'], dialog: { title: 'Styles' } }))
        .kind,
    ).toBe('forbidden');
    // And the Model dialog allows no other control but Close.
    expect(classify(facts({ name: 'Share', dialog: model })).kind).toBe('forbidden');
    expect(classify(facts({ name: 'Close', dialog: model })).kind).toBe('allowed');
  });
});

describe('the forbidden-control matcher, by rule', () => {
  it.each(['Create', 'create song', 'PUBLISH', 'Delete', 'Trash', 'Move to Trash', 'Remove x'])(
    'forbids a button or menu item named %s',
    (name) => {
      expect(classify(facts({ name })).kind).toBe('forbidden');
      expect(classify(facts({ role: 'menuitem', name })).kind).toBe('forbidden');
    },
  );

  it('reads the aria-label, the text, and the title as well as the accessible name', () => {
    expect(classify(facts({ otherNames: ['Delete clip'] })).kind).toBe('forbidden');
    expect(classify(facts({ otherNames: ['', 'Publish'] })).kind).toBe('forbidden');
  });

  it('matches whole leading words, on buttons and menu items only', () => {
    expect(classify(facts({ name: 'Recreate' })).kind).toBe('allowed');
    expect(classify(facts({ name: 'Creates' })).kind).toBe('allowed');
    expect(classify(facts({ role: 'link', name: 'Create' })).kind).toBe('allowed');
    expect(classify(facts({ role: 'tab', name: 'Delete' })).kind).toBe('allowed');
  });

  it('forbids a known selector whatever the role or name', () => {
    expect(
      classify(facts({ role: null, name: '', matches: (s) => s === '[aria-label="Create song"]' })),
    ).toEqual({ kind: 'forbidden', reason: 'it is the Create button (Songs and Sounds)' });
  });

  it('fails closed in a dialog it does not recognise, and allows only the listed controls in one it does', () => {
    expect(classify(facts({ dialog: { title: 'Rename' }, name: 'Save' })).kind).toBe('forbidden');
    expect(classify(facts({ dialog: { title: '' }, name: 'Close' })).kind).toBe('forbidden');
    expect(
      classify(facts({ dialog: { title: 'Overwrite Lyrics & Styles?' }, name: 'Overwrite' })).kind,
    ).toBe('allowed');
    expect(
      classify(facts({ dialog: { title: 'Overwrite Lyrics & Styles?' }, name: 'Close' })).kind,
    ).toBe('forbidden');
  });

  it('recognises the exception by its dialog, not by the button text', () => {
    const confirm = facts({ name: 'Confirm', inlineField: () => 'New workspace name' });
    expect(classify(confirm).kind).toBe('exception');
    // Complement: a Confirm elsewhere, or in a real dialog, is not the exception.
    expect(classify(facts({ name: 'Confirm', inlineField: () => 'Search' })).kind).toBe('allowed');
    expect(classify({ ...confirm, dialog: { title: 'Delete workspace?' } }).kind).toBe('forbidden');
  });

  it('forbids a press that would submit a form', () => {
    expect(classify(facts({ submitsForm: true })).kind).toBe('forbidden');
  });
});

describe('the facts the primitives gather', () => {
  it('forbids a control inside a forbidden one, since the click reaches it too', () => {
    document.body.innerHTML =
      '<button aria-label="Create song"><span role="button">Go</span></button>';
    const inner = document.querySelector('span');
    expect(inner && verdictOf(inner)).toEqual({
      kind: 'forbidden',
      reason: 'it is inside a control: it is the Create button (Songs and Sounds)',
    });
  });

  it("titles a dialog by its name, else by its first heading's", () => {
    document.body.innerHTML =
      '<div role="dialog"><h2>Delete clip?</h2><button>Yes</button></div>' +
      '<div role="dialog" aria-label="Overwrite Lyrics &amp; Styles?"><button>Overwrite</button></div>';
    const [yes, overwrite] = [...document.querySelectorAll('button')];
    expect(yes && verdictOf(yes)).toEqual({
      kind: 'forbidden',
      reason: 'it is in a dialog the adapter does not recognise',
    });
    expect(overwrite && verdictOf(overwrite)).toEqual({ kind: 'allowed' });
  });

  it("forbids a form's submit button and leaves a plain button alone", () => {
    document.body.innerHTML =
      '<form><button id="submit">Go</button><button id="plain" type="button">Go</button></form>';
    const submit = document.getElementById('submit');
    const plain = document.getElementById('plain');
    expect(submit && verdictOf(submit).kind).toBe('forbidden');
    expect(plain && verdictOf(plain).kind).toBe('allowed');
  });
});

describe('the Save to picker (#435)', () => {
  const field = SAVE_TO_PICKER.field;
  const picker = { title: '', controls: [field, '', 'My Workspace (81 clips)'] };

  it('allows a workspace’s row and its box, and refuses “+”, a “Create …” row, and anything else, in a dialog', () => {
    expect(classify(facts({ name: 'My Workspace (81 clips)', dialog: picker }))).toEqual({
      kind: 'allowed',
    });
    expect(
      classify(facts({ role: 'option', name: 'Sound test (1 clip)', dialog: picker })).kind,
    ).toBe('allowed');
    expect(classify(facts({ role: 'textbox', name: field, dialog: picker })).kind).toBe('allowed');
    expect(classify(facts({ name: '', dialog: picker }))).toEqual({
      kind: 'forbidden',
      reason: 'the Save to picker allows only a workspace’s row: its “+” may create a workspace',
    });
    expect(classify(facts({ name: '+', otherNames: ['+'], dialog: picker })).kind).toBe(
      'forbidden',
    );
    expect(classify(facts({ name: 'Create “Night Drive”', dialog: picker }))).toEqual({
      kind: 'forbidden',
      reason: 'its name starts with Create, Publish, Delete, Trash, or Remove',
    });
    // A workspace's name is not enough: its count of clips is part of a row's name.
    expect(classify(facts({ name: 'My Workspace', dialog: picker })).kind).toBe('forbidden');
  });

  it('knows the picker outside a dialog by its box beside the control, and keeps the workspace exception out of it', () => {
    const beside = { fieldOnPage: () => true, inlineField: () => field };
    expect(classify(facts({ name: '', ...beside })).kind).toBe('forbidden');
    expect(classify(facts({ name: 'Country Farming (137 clips)', ...beside })).kind).toBe(
      'allowed',
    );
    expect(classify(facts({ name: 'Create new workspace', ...beside })).kind).toBe('forbidden');
    // Elsewhere, the list's own entry is still the exception, and a plain button allowed.
    expect(classify(facts({ name: 'Create new workspace' }))).toEqual({
      kind: 'exception',
      exception: 'create-workspace',
    });
    expect(classify(facts({ name: '', fieldOnPage: () => true })).kind).toBe('allowed');
  });

  it('on its snapshot, refuses “+” and allows each row', () => {
    load(SAVE_TO_PICKER.snapshot);
    const dialog = document.querySelector('[role="dialog"]');
    const buttons = [...(dialog?.querySelectorAll('button') ?? [])];
    expect(buttons.map((button) => [nameOf(button), verdictOf(button).kind])).toEqual([
      ['', 'forbidden'],
      ['My Workspace (81 clips)', 'allowed'],
      ['Practice Room Sketches (18 clips)', 'allowed'],
      ['Country Farming (137 clips)', 'allowed'],
      ['Sound test (2 clips)', 'allowed'],
    ]);
  });
});

describe('the “Add a song to mashup” dialog (#438, TS-007-18)', () => {
  const dialog = { title: MASHUP_PICKER.title };
  const REFUSED = { kind: 'forbidden', reason: 'the dialog it is in does not allow it' };

  it('allows a song’s “Add … to mashup”, the “Search clips” box, and Close', () => {
    for (const name of ['Add The Long Thaw to mashup', 'Add Harbor Lights (Live) to mashup']) {
      expect(classify(facts({ name, dialog })).kind, name).toBe('allowed');
    }
    expect(classify(facts({ name: 'Close', dialog })).kind).toBe('allowed');
    expect(classify(facts({ role: 'textbox', name: 'Search clips', dialog })).kind).toBe('allowed');
  });

  it('refuses Edit title, like, play, the rows themselves, the tabs, filter, sort, the liked filter, and Create, Publish, or Delete', () => {
    for (const [role, name] of [
      ['button', 'Edit title'],
      ['button', 'Like The Long Thaw'],
      ['button', 'Play The Long Thaw'],
      ['button', 'The Long Thaw. Double-tap or press Enter to add to mashup.'],
      // A row whose title starts like its button is still a row: its name ends with a stop.
      ['button', 'Add The Long Thaw to mashup. Double-tap or press Enter to add to mashup.'],
      ['tab', 'My Library'],
      ['tab', 'Everything'],
      ['button', '(3)'],
      ['button', 'New'],
      ['button', 'Filter to show only liked clips'],
      ['button', ''],
      ['button', 'Select'],
      ['button', 'Add to mashup'],
      ['button', 'add The Long Thaw to mashup'],
      ['button', 'Create'],
      ['button', 'Publish'],
      ['button', 'Delete'],
      // Named as a song's Add, but not a button: a link or an image.
      ['link', 'Add The Long Thaw to mashup'],
      ['img', 'Add The Long Thaw to mashup'],
      // The placeholder is no longer the box's name: Suno names it "Search clips".
      ['textbox', 'Search for a song in your library...'],
    ] as const) {
      expect(classify(facts({ role, name, dialog })), `${role} "${name}"`).toEqual(REFUSED);
    }
    // An Add whose own words start with a forbidden one is refused for them, wherever it is.
    expect(
      classify(facts({ name: 'Add The Long Thaw to mashup', otherNames: ['Delete'], dialog })),
    ).toEqual({
      kind: 'forbidden',
      reason: 'its name starts with Create, Publish, Delete, Trash, or Remove',
    });
  });

  it('on its snapshots, allows only a song’s Add, Close, and the search box', () => {
    for (const snapshot of [MASHUP_PICKER.snapshot, 'create-source-mashup-add-song-search']) {
      load(snapshot);
      const open = document.querySelector('[role="dialog"]');
      const inDialog = [...(open?.querySelectorAll('*') ?? [])].filter(
        (element) => roleOf(element) !== null,
      );
      const words = (kind: string) => [
        ...new Set(
          inDialog
            .filter((element) => verdictOf(element).kind === kind)
            .map((element) => `${roleOf(element) ?? ''} "${nameOf(element)}"`),
        ),
      ];
      const titles = [
        ...new Set(
          [...(open?.querySelectorAll('a[href^="/song/"]') ?? [])].map((link) => link.textContent),
        ),
      ];
      // The tab list and the row group hold controls rather than being ones.
      expect(words('allowed').toSorted(), snapshot).toEqual(
        [
          'button "Close"',
          'rowgroup ""',
          'tablist ""',
          'textbox "Search clips"',
          ...titles.map((title) => `button "Add ${title} to mashup"`),
        ].toSorted(),
      );
      expect(words('forbidden').toSorted(), snapshot).toEqual(
        [
          'button "(3)"',
          'button "Edit title"',
          'button "Filter to show only liked clips"',
          'button "New"',
          'tab "Everything"',
          'tab "My Library"',
          ...titles.flatMap((title) => [
            `button "${title}. Double-tap or press Enter to add to mashup."`,
            `button "Like ${title}"`,
            `button "Play ${title}"`,
            // Not controls, but nothing in the dialog may be pressed unless it allows it.
            `img "${title} artwork"`,
            `link "${title}"`,
          ]),
        ].toSorted(),
      );
    }
  });
});

describe('Suno’s “Replace Lyrics?” question after a Mashup’s second song (#438, TS-007-19)', () => {
  const dialog = { title: REPLACE_LYRICS.title };
  const REFUSED = { kind: 'forbidden', reason: 'the dialog it is in does not allow it' };

  it('allows only Keep Current Lyrics and Close', () => {
    expect(classify(facts({ name: 'Keep Current Lyrics', dialog })).kind).toBe('allowed');
    expect(classify(facts({ name: 'Close', dialog })).kind).toBe('allowed');
    for (const name of [
      'Mashup Lyrics',
      'Use Lyrics from "The Long Thaw"',
      'Use Lyrics from “The Long Thaw”',
      'Keep Current',
      'Overwrite',
      'Create',
      'Publish',
      'Delete',
    ]) {
      expect(classify(facts({ name, dialog })), name).toEqual(REFUSED);
    }
  });

  it('on its snapshot, allows Keep Current Lyrics and Close, and refuses Mashup Lyrics and Use Lyrics from …', () => {
    load(REPLACE_LYRICS.snapshot);
    const open = document.querySelector('[role="dialog"]');
    const verdicts = Object.fromEntries(
      [...(open?.querySelectorAll('*') ?? [])]
        .filter((element) => roleOf(element) !== null)
        .map((element) => [nameOf(element), verdictOf(element).kind]),
    );
    expect(verdicts).toEqual({
      Close: 'allowed',
      'Use Lyrics from "The Long Thaw"': 'forbidden',
      'Keep Current Lyrics': 'allowed',
      'Mashup Lyrics': 'forbidden',
    });
  });
});
