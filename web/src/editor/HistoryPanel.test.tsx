import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { EditorView } from '@codemirror/view';
import { describe, expect, it } from 'vitest';
import type { Snapshot } from '../api/snapshots';
import { formatDateTime } from '../api/timeZone';
import { renderApp } from '../test/helpers';
import { testVersion, versionServer } from '../test/versionServer';

const ONE = testVersion('1', {
  current: true,
  lyrics: '[Verse]\nRewritten line\nKept line\n',
  styles: 'metal',
});

function snapshot(id: number, createdAt: string, lyrics: string, styles: string): Snapshot {
  return {
    id: `0199b1a0-5000-7000-9000-${String(id).padStart(12, '0')}`,
    versionId: ONE.id,
    createdAt,
    lyrics,
    styles,
  };
}

const EARLY = snapshot(91, '2026-10-01T08:00:00Z', '[Verse]\nFirst line\nKept line\n', 'folk');
const LATER = snapshot(92, '2026-10-01T08:30:00Z', '[Verse]\nRewritten line\nKept line\n', 'metal');

async function openHistory() {
  const user = userEvent.setup();
  renderApp('/songs/n8-7');
  await screen.findByRole('textbox', { name: 'Lyrics' });
  await user.click(screen.getByRole('button', { name: 'Show history' }));
  return user;
}

function when(utc: string) {
  return formatDateTime(utc, 'UTC');
}

function editor(): EditorView {
  const element = screen.getByTestId('lyrics-editor').querySelector('.cm-editor');
  const view = element instanceof HTMLElement ? EditorView.findFromDOM(element) : null;
  if (view === null) {
    throw new Error('The lyrics editor is not drawn.');
  }
  return view;
}

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

describe('the History panel', () => {
  it('is closed until asked for, then says when there are no snapshots yet', async () => {
    versionServer([ONE]);
    renderApp('/songs/n8-7');
    await screen.findByRole('textbox', { name: 'Lyrics' });

    const toggle = screen.getByRole('button', { name: 'Show history' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await userEvent.setup().click(toggle);

    expect(screen.getByRole('button', { name: 'Hide history' })).toHaveAttribute(
      'aria-expanded',
      'true',
    );
    expect(await screen.findByText(/^No snapshots yet\./)).toBeVisible();
  });

  it('lists the snapshots newest first with their time, and compares one with the editor in words', async () => {
    const { server } = versionServer([ONE]);
    server.snapshots = [EARLY, LATER];
    const user = await openHistory();

    const list = await screen.findByRole('list', { name: 'Snapshots' });
    const entries = within(list).getAllByRole('button');
    expect(entries.map((entry) => entry.textContent)).toEqual([
      when(LATER.createdAt),
      when(EARLY.createdAt),
    ]);
    expect(screen.getByText(/^2 snapshots, newest first\./)).toBeVisible();

    await user.click(within(list).getByRole('button', { name: when(EARLY.createdAt) }));
    expect(within(list).getByRole('button', { name: when(EARLY.createdAt) })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    const shown = await screen.findByTestId('snapshot');
    expect(
      within(shown).getByRole('heading', { name: `Snapshot from ${when(EARLY.createdAt)}` }),
    ).toBeVisible();

    // Each changed line says whether it was removed or added since the snapshot, as text.
    const lyrics = within(shown).getByRole('list', {
      name: 'Lyrics: changes from the snapshot to the editor now',
    });
    expect(
      within(lyrics)
        .getAllByRole('listitem')
        .map((line) => line.textContent),
    ).toEqual(['  [Verse]', '− Removed: First line', '+ Added: Rewritten line', '  Kept line']);
    const styles = within(shown).getByRole('list', {
      name: 'Styles: changes from the snapshot to the editor now',
    });
    expect(
      within(styles)
        .getAllByRole('listitem')
        .map((line) => line.textContent),
    ).toEqual(['− Removed: folk', '+ Added: metal']);

    // The comparison is with the editor's text as it is now, saved or not.
    typeLyrics('New ending');
    await waitFor(() => {
      expect(within(lyrics).getAllByRole('listitem').at(-1)).toHaveTextContent(
        '+ Added: New ending',
      );
    });

    // The newer snapshot holds what was saved before typing: only the new line differs.
    await user.click(within(list).getByRole('button', { name: when(LATER.createdAt) }));
    const later = await screen.findByRole('heading', {
      name: `Snapshot from ${when(LATER.createdAt)}`,
    });
    expect(later).toBeVisible();
    expect(screen.getAllByText('The same as in the editor now.')).toHaveLength(1);
  });

  it('restores a snapshot after confirmation; the replaced text is then in History', async () => {
    const { server } = versionServer([ONE]);
    server.snapshots = [EARLY];
    const user = await openHistory();
    await user.click(await screen.findByRole('button', { name: when(EARLY.createdAt) }));
    await screen.findByTestId('snapshot');

    // Cancel changes nothing.
    await user.click(screen.getByRole('button', { name: 'Restore this snapshot' }));
    const confirm = await screen.findByRole('dialog', { name: 'Restore this snapshot?' });
    expect(confirm).toHaveTextContent('kept in History first');
    await user.click(within(confirm).getByRole('button', { name: 'Cancel' }));
    expect(server.writes).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: 'Restore this snapshot' }));
    await user.click(
      within(await screen.findByRole('dialog', { name: 'Restore this snapshot?' })).getByRole(
        'button',
        { name: 'Restore' },
      ),
    );

    await waitFor(() => {
      expect(editor().state.doc.toString()).toBe(EARLY.lyrics);
    });
    expect(screen.getByRole('textbox', { name: 'Styles' })).toHaveValue('folk');
    expect(server.writes).toEqual([{ method: 'POST', path: `restore ${EARLY.id}`, body: {} }]);
    expect(
      await screen.findByText(
        `Restored the snapshot from ${when(EARLY.createdAt)}. The text it replaced is kept in History.`,
      ),
    ).toBeVisible();
    expect(indicator()).toHaveTextContent(/^Saved$/);

    // The list is read again: the text just replaced is its newest entry.
    const list = screen.getByRole('list', { name: 'Snapshots' });
    await waitFor(() => {
      expect(within(list).getAllByRole('button')).toHaveLength(2);
    });
    expect(server.snapshots.at(-1)).toMatchObject({ lyrics: ONE.lyrics, styles: ONE.styles });
    expect(screen.getAllByText('The same as in the editor now.')).toHaveLength(2);
  });

  it('turns Restore off, saying why, while the editor has unsaved changes', async () => {
    const { server } = versionServer([ONE]);
    server.snapshots = [EARLY];
    const user = await openHistory();
    await user.click(await screen.findByRole('button', { name: when(EARLY.createdAt) }));
    await screen.findByTestId('snapshot');
    expect(screen.getByRole('button', { name: 'Restore this snapshot' })).toBeEnabled();

    // The save fails: the work is not stored, and Restore says so.
    server.next = () => Promise.reject(new TypeError('Failed to fetch'));
    typeLyrics('Unsaved');
    const restore = screen.getByRole('button', { name: 'Restore this snapshot' });
    await waitFor(() => {
      expect(restore).toBeDisabled();
    });
    expect(restore).toHaveAccessibleDescription(
      'Restore is unavailable while your changes are not saved. It is available again once they are.',
    );

    // Once saved, it is available again.
    await waitFor(
      () => {
        expect(indicator()).toHaveTextContent(/^Saved$/);
      },
      { timeout: 6_000 },
    );
    expect(restore).toBeEnabled();
  }, 10_000);

  it('turns Restore off while a conflict is undecided, and Reload snapshots the discarded text first', async () => {
    const { server } = versionServer([ONE]);
    server.snapshots = [EARLY];
    const user = await openHistory();
    await user.click(await screen.findByRole('button', { name: when(EARLY.createdAt) }));
    await screen.findByTestId('snapshot');
    server.changeElsewhere('1', { lyrics: 'Theirs' });

    typeLyrics('Mine');
    const dialog = await screen.findByRole(
      'dialog',
      { name: 'Changed since you loaded it' },
      { timeout: 4_000 },
    );
    await user.click(within(dialog).getByRole('button', { name: 'Keep editing' }));
    const restore = screen.getByRole('button', { name: 'Restore this snapshot' });
    await waitFor(() => {
      expect(restore).toHaveAccessibleDescription(
        /^Restore is unavailable while this Version has a conflict/,
      );
    });
    expect(restore).toBeDisabled();

    await user.click(
      within(screen.getByTestId('autosave')).getByRole('button', { name: 'Reload' }),
    );
    await waitFor(() => {
      expect(editor().state.doc.toString()).toBe('Theirs');
    });
    // What was discarded reached history before it was replaced.
    await waitFor(() => {
      expect(server.snapshots.map((kept) => kept.lyrics)).toContain(`${ONE.lyrics}Mine`);
    });
    expect(restore).toBeEnabled();
  });

  it('says so when the Version changed elsewhere before a restore, and shows its current text', async () => {
    const { server } = versionServer([ONE]);
    server.snapshots = [EARLY];
    const user = await openHistory();
    await user.click(await screen.findByRole('button', { name: when(EARLY.createdAt) }));
    await screen.findByTestId('snapshot');
    server.changeElsewhere('1', { lyrics: 'Changed by a tool' });

    await user.click(screen.getByRole('button', { name: 'Restore this snapshot' }));
    await user.click(
      within(await screen.findByRole('dialog', { name: 'Restore this snapshot?' })).getByRole(
        'button',
        { name: 'Restore' },
      ),
    );

    expect(
      await screen.findByText(/^Not restored: this Version was changed elsewhere\./),
    ).toBeVisible();
    await waitFor(() => {
      expect(editor().state.doc.toString()).toBe('Changed by a tool');
    });
    expect(server.versions[0]?.lyrics).toBe('Changed by a tool');
  });
});
