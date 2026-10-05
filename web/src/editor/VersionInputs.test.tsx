import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { EditorView } from '@codemirror/view';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../test/helpers';
import { testVersion, versionServer } from '../test/versionServer';

const ONE = testVersion('1', { current: true, lyrics: '[Verse]\nRun (ooh)\n', styles: 'punk' });

async function openVersion(path = '/songs/n8-7') {
  const rendered = renderApp(path);
  await screen.findByRole('textbox', { name: 'Lyrics' });
  return rendered;
}

/** The lyrics editor's CodeMirror view. */
function editor(): EditorView {
  const element = screen.getByTestId('lyrics-editor').querySelector('.cm-editor');
  const view = element instanceof HTMLElement ? EditorView.findFromDOM(element) : null;
  if (view === null) {
    throw new Error('The lyrics editor is not drawn.');
  }
  return view;
}

/** Types `text` at the end of the lyrics, as keystrokes arrive in CodeMirror. */
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

/** Replaces the lyrics, as a paste does. */
function pasteLyrics(text: string) {
  const view = editor();
  act(() => {
    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: text },
      userEvent: 'input.paste',
    });
  });
}

function saveButton() {
  return screen.getByRole('button', { name: 'Save lyrics and styles' });
}

describe('the lyrics editor', () => {
  it('shows the lyrics, labelled, with tags and parentheticals marked differently', async () => {
    versionServer([ONE]);
    await openVersion();

    expect(editor().state.doc.toString()).toBe('[Verse]\nRun (ooh)\n');
    const tags = screen.getByTestId('lyrics-editor').querySelectorAll('.cm-lyrics-tag');
    const parentheticals = screen
      .getByTestId('lyrics-editor')
      .querySelectorAll('.cm-lyrics-parenthetical');
    expect([...tags].map((tag) => tag.textContent)).toEqual(['[Verse]']);
    expect([...parentheticals].map((mark) => mark.textContent)).toEqual(['(ooh)']);
    expect(screen.getByRole('textbox', { name: 'Styles' })).toHaveValue('punk');
    expect(screen.getByText('No warnings.')).toBeVisible();
    expect(saveButton()).toBeDisabled();
  });

  it('offers the common tags on [, inserts the whole tag, and can be dismissed', async () => {
    const user = userEvent.setup();
    versionServer([ONE]);
    await openVersion();

    typeLyrics('[');
    const list = await screen.findByRole('listbox');
    expect(
      within(list)
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toContain('Chorus');

    typeLyrics('ch');
    await waitFor(() => {
      expect(within(screen.getByRole('listbox')).getAllByRole('option')).toHaveLength(1);
    });
    await user.keyboard('{Enter}');
    expect(editor().state.doc.toString()).toBe('[Verse]\nRun (ooh)\n[Chorus]');
    expect(screen.queryByRole('listbox')).toBeNull();

    // Escape dismisses the list; the user can type any tag.
    typeLyrics('\n[');
    await screen.findByRole('listbox');
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('listbox')).toBeNull();
    });
    typeLyrics('Whisper softly, building]');
    expect(editor().state.doc.toString()).toBe(
      '[Verse]\nRun (ooh)\n[Chorus]\n[Whisper softly, building]',
    );
    expect(screen.getByText('No warnings.')).toBeVisible();
  });

  it('warns on an unclosed bracket on its line, exposed to assistive technology, and still saves', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    await openVersion();

    typeLyrics('[Bridge');

    const marker = await screen.findByRole('button', {
      name: /^Warning, line 3: This \[ is not closed/,
    });
    expect(marker).toBeVisible();
    expect(screen.getByText('1 warning (they do not stop saving):')).toHaveAttribute(
      'role',
      'status',
    );
    const list = screen.getByRole('list', { name: 'Lyrics warnings' });
    expect(list).toHaveTextContent('Line 3: This [ is not closed on its line.');
    // The editor is described by the warnings.
    const describedBy =
      screen.getByRole('textbox', { name: 'Lyrics' }).getAttribute('aria-describedby') ?? '';
    expect(describedBy.split(' ').some((id) => document.getElementById(id)?.contains(list))).toBe(
      true,
    );

    expect(saveButton()).toBeEnabled();
    await user.click(saveButton());

    expect(await screen.findByText('Saved.')).toBeVisible();
    expect(server.writes.map((write) => write.body)).toEqual([
      { lyrics: '[Verse]\nRun (ooh)\n[Bridge' },
    ]);
    expect(saveButton()).toBeDisabled();
  });

  it('saves lyrics exactly as typed and styles in one PATCH, also by Ctrl+S', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    await openVersion();

    typeLyrics('  \t\n\n');
    const styles = screen.getByRole('textbox', { name: 'Styles' });
    await user.type(styles, ', fast ');
    expect(screen.getByText('Unsaved changes.')).toBeVisible();
    await user.keyboard('{Control>}s{/Control}');

    expect(await screen.findByText('Saved.')).toBeVisible();
    expect(server.writes.map((write) => write.body)).toEqual([
      { lyrics: '[Verse]\nRun (ooh)\n  \t\n\n', styles: 'punk, fast ' },
    ]);
    expect(server.versions[0]?.revision).toBe(2);
  });

  it('counts over the limit, says so, and refuses to save until it is back under', async () => {
    const user = userEvent.setup();
    versionServer([ONE]);
    await openVersion();

    pasteLyrics('x'.repeat(5_001));

    expect(await screen.findByText('5,001 / 5,000 characters')).toBeVisible();
    expect(screen.getByRole('alert')).toHaveTextContent('Over the limit by 1 character.');
    expect(saveButton()).toBeDisabled();

    pasteLyrics('x'.repeat(5_000));
    expect(await screen.findByText('5,000 / 5,000 characters')).toBeVisible();
    expect(saveButton()).toBeEnabled();

    await user.type(screen.getByRole('textbox', { name: 'Styles' }), 'y'.repeat(997));
    expect(screen.getByText(/Over the limit by 1 character\./)).toBeVisible();
    expect(saveButton()).toBeDisabled();
  });

  it('opens the shared conflict dialog when the lyrics changed elsewhere', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    await openVersion();
    server.changeElsewhere('1', { lyrics: 'Theirs' });

    typeLyrics('Mine');
    await user.click(saveButton());

    const dialog = await screen.findByRole('dialog', { name: 'Changed since you loaded it' });
    expect(within(dialog).getByText('Theirs')).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Reload' }));

    await waitFor(() => {
      expect(editor().state.doc.toString()).toBe('Theirs');
    });
    expect(saveButton()).toBeDisabled();
  });
});

describe('leaving with unsaved changes', () => {
  const TWO = testVersion('2', { lyrics: 'Two' });

  it('asks first: Stay keeps the edit, Discard leaves it', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, TWO]);
    const { router } = await openVersion();

    typeLyrics('More');
    await user.click(screen.getByRole('treeitem', { name: /Version 2/ }));

    const dialog = await screen.findByRole('dialog', { name: 'Unsaved lyrics or styles' });
    await user.click(within(dialog).getByRole('button', { name: 'Stay' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(router.state.location.pathname).toBe('/songs/n8-7');
    expect(editor().state.doc.toString()).toBe('[Verse]\nRun (ooh)\nMore');

    await user.click(screen.getByRole('treeitem', { name: /Version 2/ }));
    await user.click(
      within(await screen.findByRole('dialog', { name: 'Unsaved lyrics or styles' })).getByRole(
        'button',
        { name: 'Discard' },
      ),
    );

    expect(await screen.findByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    await waitFor(() => {
      expect(editor().state.doc.toString()).toBe('Two');
    });
    expect(server.writes).toEqual([]);
  });

  it('saves and continues', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, TWO]);
    const { router } = await openVersion();

    typeLyrics('More');
    act(() => {
      void router.navigate('/songs');
    });
    const dialog = await screen.findByRole('dialog', { name: 'Unsaved lyrics or styles' });
    await user.click(within(dialog).getByRole('button', { name: 'Save and continue' }));

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/songs');
    });
    expect(server.writes.map((write) => write.body)).toEqual([
      { lyrics: '[Verse]\nRun (ooh)\nMore' },
    ]);
  });

  it('does not ask when nothing changed', async () => {
    const user = userEvent.setup();
    versionServer([ONE, TWO]);
    await openVersion();

    await user.click(screen.getByRole('treeitem', { name: /Version 2/ }));

    expect(await screen.findByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
