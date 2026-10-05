import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { EditorView } from '@codemirror/view';
import { describe, expect, it } from 'vitest';
import type { Snapshot } from '../api/snapshots';
import { formatDateTime } from '../api/timeZone';
import { jsonResponse, renderApp } from '../test/helpers';
import { testVersion, versionServer } from '../test/versionServer';
import { FROZEN_NOTICE_TEXT } from './FrozenNotice';

const STORED = { lyrics: '[Verse]\nGenerated line\n', styles: 'synthwave' };
const FROZEN = testVersion('1', { current: true, isFrozen: true, revision: 3, ...STORED });
const MUTABLE = testVersion('1', { current: true, ...STORED });

async function openVersion(path = '/songs/n8-7') {
  const user = userEvent.setup();
  const rendered = renderApp(path);
  await screen.findByRole('textbox', { name: 'Lyrics' });
  return { user, ...rendered };
}

function editor(): EditorView {
  const element = screen.getByTestId('lyrics-editor').querySelector('.cm-editor');
  const view = element instanceof HTMLElement ? EditorView.findFromDOM(element) : null;
  if (view === null) {
    throw new Error('The lyrics editor is not drawn.');
  }
  return view;
}

/** Types `text` at the end of the lyrics, as keystrokes arrive in CodeMirror (a read-only editor ignores them). */
function typeLyrics(text: string) {
  const view = editor();
  act(() => {
    view.focus();
    const end = view.state.doc.length;
    view.dispatch({
      changes: { from: end, insert: text },
      selection: { anchor: end + text.length },
      userEvent: 'input.type',
    });
  });
}

function indicator() {
  return within(screen.getByTestId('autosave')).getByRole('status');
}

async function writesReach(writes: unknown[], count: number, timeout = 4_000) {
  await waitFor(
    () => {
      expect(writes).toHaveLength(count);
    },
    { timeout },
  );
}

describe('a frozen Version', () => {
  it('shows its lyrics and styles read only, with the notice and its Create New Version From action', async () => {
    const { server } = versionServer([FROZEN]);
    const { user } = await openVersion();

    const lyrics = screen.getByRole('textbox', { name: 'Lyrics' });
    expect(lyrics).toHaveAttribute('aria-readonly', 'true');
    expect(editor().state.readOnly).toBe(true);
    expect(screen.getByRole('textbox', { name: 'Styles' })).toHaveAttribute('readonly');
    expect(screen.getByText(/^Read only\./)).toBeVisible();

    const notice = screen.getByTestId('frozen-notice');
    expect(within(notice).getByText(FROZEN_NOTICE_TEXT)).toBeVisible();
    // Nothing was carried: the notice says nothing about unsaved text.
    expect(within(notice).queryByText(/unsaved changes/)).toBeNull();

    // Typing into the styles field changes nothing, and nothing is sent.
    await user.type(screen.getByRole('textbox', { name: 'Styles' }), 'x');
    expect(screen.getByRole('textbox', { name: 'Styles' })).toHaveValue('synthwave');

    await user.click(within(notice).getByRole('button', { name: 'Create New Version From 1' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create New Version From 1' });
    // The proposal is preselected; nothing changes until the user confirms.
    expect(await within(dialog).findByRole('radio', { name: '2 (proposed)' })).toBeChecked();
    await waitFor(() => {
      expect(within(dialog).getByText(/starts with a copy of 1’s lyrics and styles/)).toBeVisible();
    });
    expect(server.writes).toHaveLength(0);
  });

  it('still saves its name and notes', async () => {
    const { server } = versionServer([FROZEN]);
    const { user } = await openVersion();

    await user.type(screen.getByRole('textbox', { name: 'Name' }), 'Released take');
    await writesReach(server.writes, 1);
    expect(server.writes[0]?.body).toEqual({ name: 'Released take' });
    await waitFor(() => {
      expect(indicator()).toHaveTextContent(/^Saved$/);
    });
  });

  it('is not a mutable Version: one without a Generation has no notice and is editable', async () => {
    versionServer([MUTABLE]);
    await openVersion();

    expect(screen.queryByTestId('frozen-notice')).toBeNull();
    expect(screen.getByRole('textbox', { name: 'Lyrics' })).not.toHaveAttribute('aria-readonly');
    expect(editor().state.readOnly).toBe(false);
    expect(screen.getByRole('textbox', { name: 'Styles' })).not.toHaveAttribute('readonly');
  });
});

describe('the Version tree', () => {
  it('shows a lock, with an accessible label, on frozen Versions only', async () => {
    versionServer([FROZEN, testVersion('2', { name: 'Open take' })]);
    await openVersion();

    const frozen = screen.getByRole('treeitem', { name: /^Version 1,/ });
    expect(frozen).toHaveAccessibleName('Version 1, current working Version, frozen');
    expect(within(frozen).getByRole('img', { name: 'Frozen: has a Generation' })).toBeVisible();

    const open = screen.getByRole('treeitem', { name: /^Version 2,/ });
    expect(open).toHaveAccessibleName('Version 2, Open take');
    expect(within(open).queryByRole('img')).toBeNull();
  });
});

describe('History on a frozen Version', () => {
  const SNAPSHOT: Snapshot = {
    id: '0199b1a0-5000-7000-9000-000000000091',
    versionId: FROZEN.id,
    createdAt: '2026-10-01T08:00:00Z',
    lyrics: '[Verse]\nEarlier line\n',
    styles: 'folk',
  };

  it('stays readable and comparable, and offers Restore into a new Version instead of Restore', async () => {
    const { server } = versionServer([FROZEN]);
    server.snapshots = [SNAPSHOT];
    const { user } = await openVersion();
    await user.click(screen.getByRole('button', { name: 'Show history' }));

    const list = await screen.findByRole('list', { name: 'Snapshots' });
    await user.click(
      within(list).getByRole('button', { name: formatDateTime(SNAPSHOT.createdAt, 'UTC') }),
    );
    const shown = await screen.findByTestId('snapshot');
    expect(
      within(
        within(shown).getByRole('list', {
          name: 'Lyrics: changes from the snapshot to the editor now',
        }),
      )
        .getAllByRole('listitem')
        .map((line) => line.textContent),
    ).toEqual(['  [Verse]', '− Removed: Earlier line', '+ Added: Generated line']);
    expect(within(shown).queryByRole('button', { name: 'Restore this snapshot' })).toBeNull();

    await user.click(within(shown).getByRole('button', { name: 'Restore into a new Version' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create New Version From 1' });
    await within(dialog).findByRole('radio', { name: '2 (proposed)' });
    await waitFor(() => {
      expect(
        within(dialog).getByText(/starts with the lyrics and styles carried over/),
      ).toBeVisible();
    });
    await user.click(within(dialog).getByRole('button', { name: 'Create Version' }));

    // The new Version is a create-from of 1 carrying the snapshot's text; it is current, selected, and editable.
    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Version 2' })).toBeVisible();
    });
    expect(server.writes.filter((write) => write.method === 'POST')).toEqual([
      expect.objectContaining({
        body: {
          sourceVersionId: FROZEN.id,
          number: '2',
          name: '',
          lyrics: SNAPSHOT.lyrics,
          styles: SNAPSHOT.styles,
        },
      }),
    ]);
    await waitFor(() => {
      expect(editor().state.doc.toString()).toBe(SNAPSHOT.lyrics);
    });
    expect(editor().state.readOnly).toBe(false);
    expect(screen.queryByTestId('frozen-notice')).toBeNull();
    // Version 1 is unchanged.
    expect(server.versions[0]).toMatchObject(STORED);
  });
});

describe('a Version that freezes while its editor is open', () => {
  it('on a 409 whose current Version is frozen: no conflict dialog, the notice, and the unsaved text carried to a new Version', async () => {
    const { server } = versionServer([MUTABLE]);
    const { user } = await openVersion();
    // A Generation is attached elsewhere: the revision moves and the Version is frozen.
    server.changeElsewhere('1', { isFrozen: true });

    typeLyrics('Typed as it froze');

    const notice = await screen.findByTestId('frozen-notice', {}, { timeout: 4_000 });
    expect(within(notice).getByText(FROZEN_NOTICE_TEXT)).toBeVisible();
    expect(within(notice).getByText(/Your unsaved changes to the lyrics and styles/)).toBeVisible();
    expect(screen.queryByRole('dialog')).toBeNull();
    // The editor shows the stored text, read only; the indicator does not say "Not saved".
    expect(editor().state.doc.toString()).toBe(STORED.lyrics);
    expect(editor().state.readOnly).toBe(true);
    await waitFor(() => {
      expect(indicator()).toHaveTextContent(/^Saved$/);
    });
    // The text is also kept in History.
    await waitFor(() => {
      expect(server.snapshots.map((snapshot) => snapshot.lyrics)).toContain(
        `${STORED.lyrics}Typed as it froze`,
      );
    });

    await user.click(within(notice).getByRole('button', { name: 'Create New Version From 1' }));
    const dialog = await screen.findByRole('dialog', { name: 'Create New Version From 1' });
    await within(dialog).findByRole('radio', { name: '2 (proposed)' });
    await waitFor(() => {
      expect(
        within(dialog).getByText(/starts with the lyrics and styles carried over/),
      ).toBeVisible();
    });
    await user.click(within(dialog).getByRole('button', { name: 'Create Version' }));

    await waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Version 2' })).toBeVisible();
    });
    const created = server.writes.find((write) => write.method === 'POST');
    expect(created?.body).toMatchObject({
      lyrics: `${STORED.lyrics}Typed as it froze`,
      styles: STORED.styles,
    });
    await waitFor(() => {
      expect(editor().state.doc.toString()).toBe(`${STORED.lyrics}Typed as it froze`);
    });
  }, 10_000);

  it('keeps saving the name and notes typed with the text that could not go in', async () => {
    const { server } = versionServer([MUTABLE]);
    const { user } = await openVersion();
    server.changeElsewhere('1', { isFrozen: true });

    await user.type(screen.getByRole('textbox', { name: 'Name' }), 'Kept name');
    typeLyrics('Lost?');

    await screen.findByTestId('frozen-notice', {}, { timeout: 4_000 });
    await waitFor(() => {
      expect(server.versions[0]?.name).toBe('Kept name');
    });
    expect(server.versions[0]?.lyrics).toBe(STORED.lyrics);
    expect(screen.queryByRole('dialog')).toBeNull();
  }, 10_000);

  it('on a 409 version_frozen on its own revision: the notice too, and the text carried', async () => {
    const { server } = versionServer([MUTABLE]);
    await openVersion();
    server.next = () => jsonResponse(409, { code: 'version_frozen', versionId: MUTABLE.id });

    typeLyrics('Too late');

    const notice = await screen.findByTestId('frozen-notice', {}, { timeout: 4_000 });
    expect(within(notice).getByText(/Your unsaved changes/)).toBeVisible();
    expect(editor().state.readOnly).toBe(true);
    // The tree learns of the freeze too.
    expect(screen.getByRole('treeitem', { name: /^Version 1,/ })).toHaveAccessibleName(
      'Version 1, current working Version, frozen',
    );
  });

  it('complement: on a mutable Version the same 409 is the ordinary conflict dialog', async () => {
    const { server } = versionServer([MUTABLE]);
    await openVersion();
    server.changeElsewhere('1', { lyrics: 'Theirs' });

    typeLyrics('Mine');

    await screen.findByRole('dialog', { name: 'Changed since you loaded it' }, { timeout: 4_000 });
    expect(screen.queryByTestId('frozen-notice')).toBeNull();
    expect(editor().state.readOnly).toBe(false);
  });
});
