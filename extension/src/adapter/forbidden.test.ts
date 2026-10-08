// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { SNAPSHOT_NAMES, snapshotHtml } from '../testing/snapshots.ts';
import {
  classify,
  CREATE_WORKSPACE,
  DOWNLOAD_CLIP,
  EXCEPTIONS,
  RECOGNISED_DIALOGS,
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
      'workspace-selector: Create new workspace',
    ]);
    expect(DOWNLOAD_CLIP.snapshots).toEqual(['download-dialog']);
    // The matcher's verdict on every element of every snapshot reads jsdom's computed styles: about
    // 2.5 s here and over 5 s on a GitHub runner, so the limit is three times a CI run's ~10 s.
  }, 30_000);

  it('recognises only the Overwrite dialog and the Voice picker, by their titles', () => {
    expect(RECOGNISED_DIALOGS.map((dialog) => [dialog.title, dialog.allows])).toEqual([
      ['Overwrite Lyrics & Styles?', ['Overwrite', 'Keep Current']],
      ['Voice', ['Close', 'My Voices', 'Favorites']],
    ]);
  });

  it('still refuses everything else in the Voice picker', () => {
    load('voice-picker');
    const play = verdictsOf('button', 'Play');
    expect(play.length).toBeGreaterThan(0);
    expect(new Set(play)).toEqual(new Set(['forbidden']));
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
