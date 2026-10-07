import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { EditorView } from '@codemirror/view';
import { describe, expect, it } from 'vitest';
import { fakeTimeouts } from '../test/fakeClock';
import { jsonResponse, renderApp } from '../test/helpers';
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

/**
 * Waits for the autosave after the user's pause (1.5 s, plus any retry) to have sent `count`
 * writes, within `timeout` on the fake clock.
 */
async function writesReach(writes: unknown[], count: number, timeout = 4_000) {
  await waitFor(
    () => {
      expect(writes).toHaveLength(count);
    },
    { timeout },
  );
}

/** The autosave indicator's words. */
function indicator() {
  return within(screen.getByTestId('autosave')).getByRole('status');
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
    // Nothing to press: the indicator says the text is stored.
    expect(indicator()).toHaveTextContent(/^Saved$/);
    expect(screen.queryByRole('button', { name: /^Save/ })).toBeNull();
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

  // From here on, autosave's pause and retries run on the fake clock (fakeTimeouts), so a slow
  // machine cannot run a timed wait out before the save comes due. The two tests above stay on real
  // time: CodeMirror's completion list reads the real clock before it takes Enter.
  it('warns on an unclosed bracket on its line, exposed to assistive technology, and still saves', async () => {
    fakeTimeouts();
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

    await writesReach(server.writes, 1);
    expect(server.writes.map((write) => write.body)).toEqual([
      { lyrics: '[Verse]\nRun (ooh)\n[Bridge' },
    ]);
    await waitFor(() => {
      expect(indicator()).toHaveTextContent(/^Saved$/);
    });
  });

  it('saves automatically once the user pauses: Saving…, then Saved, in one PATCH', async () => {
    const clock = fakeTimeouts();
    const user = userEvent.setup({ advanceTimers: clock.advanceTimers });
    const { server } = versionServer([ONE]);
    await openVersion();

    typeLyrics('  \t\n\n');
    const styles = screen.getByRole('textbox', { name: 'Styles' });
    await user.type(styles, ', fast ');
    expect(indicator()).toHaveTextContent('Saving…');
    expect(server.writes).toEqual([]);

    await writesReach(server.writes, 1);
    await waitFor(() => {
      expect(indicator()).toHaveTextContent(/^Saved$/);
    });
    expect(server.writes.map((write) => write.body)).toEqual([
      { lyrics: '[Verse]\nRun (ooh)\n  \t\n\n', styles: 'punk, fast ' },
    ]);
    expect(server.versions[0]?.revision).toBe(2);
  });

  it('saves at once on Ctrl+S, without waiting for the pause', async () => {
    const clock = fakeTimeouts();
    const user = userEvent.setup({ advanceTimers: clock.advanceTimers });
    const { server } = versionServer([ONE]);
    await openVersion();

    await user.type(screen.getByRole('textbox', { name: 'Styles' }), '!');
    await user.keyboard('{Control>}s{/Control}');

    await writesReach(server.writes, 1, 500);
    expect(server.writes.map((write) => write.body)).toEqual([{ styles: 'punk!' }]);
  });

  it('counts over the limit, says so, and does not save until it is back under', async () => {
    const clock = fakeTimeouts();
    const user = userEvent.setup({ advanceTimers: clock.advanceTimers });
    const { server } = versionServer([ONE]);
    await openVersion();

    pasteLyrics('x'.repeat(5_001));

    expect(await screen.findByText('5,001 / 5,000 characters')).toBeVisible();
    expect(screen.getByRole('alert')).toHaveTextContent('Over the limit by 1 character.');
    expect(indicator()).toHaveTextContent(
      'Not saved: The lyrics are over their limit. Shorten the text to save.',
    );

    // The styles go in by one paste up to two short of their limit, then two typed keys cross it:
    // typing all 997 keys, each drawing the page again, took long enough to time out on a loaded CI.
    const styles = screen.getByRole('textbox', { name: 'Styles' });
    await user.click(styles);
    await user.paste('y'.repeat(995));
    expect(styles).toHaveValue(`punk${'y'.repeat(995)}`);
    await user.type(styles, 'yy');
    expect(screen.getAllByText(/Over the limit by 1 character\./)).toHaveLength(2);
    expect(indicator()).toHaveTextContent(
      'Not saved: The lyrics are over their limit. The styles are over their limit. Shorten the text to save.',
    );
    await clock.advanceTimersAsync(2_000);
    expect(server.writes).toEqual([]);

    pasteLyrics('x'.repeat(5_000));
    await user.type(styles, '{Backspace}');
    expect(await screen.findByText('5,000 / 5,000 characters')).toBeVisible();
    await writesReach(server.writes, 1);
    expect(server.writes.map((write) => write.body)).toEqual([
      { lyrics: 'x'.repeat(5_000), styles: `punk${'y'.repeat(996)}` },
    ]);
  });

  it('keeps the text when a save fails, says so, and retries until it is stored', async () => {
    fakeTimeouts();
    const { server } = versionServer([ONE]);
    await openVersion();
    server.next = () => jsonResponse(503, { code: 'unavailable' });

    typeLyrics('Kept');

    await waitFor(
      () => {
        expect(indicator()).toHaveTextContent(
          'Not saved: n8Tracks could not store the change. Your text is kept here, and saving is retried automatically.',
        );
      },
      { timeout: 4_000 },
    );
    expect(editor().state.doc.toString()).toBe('[Verse]\nRun (ooh)\nKept');

    // Retried two seconds later, and stored.
    await writesReach(server.writes, 2, 4_000);
    await waitFor(() => {
      expect(indicator()).toHaveTextContent(/^Saved$/);
    });
    expect(server.versions[0]?.lyrics).toBe('[Verse]\nRun (ooh)\nKept');
  }, 10_000);

  it('stops on a refusal retrying cannot fix, says what to do, and tries again when asked', async () => {
    const clock = fakeTimeouts();
    const user = userEvent.setup({ advanceTimers: clock.advanceTimers });
    const { server } = versionServer([ONE]);
    await openVersion();
    // A refusal that is not a freeze (a freeze shows the frozen notice instead: FrozenVersion.test).
    server.next = () => jsonResponse(403, { code: 'forbidden' });

    typeLyrics('Refused');
    await waitFor(
      () => {
        expect(indicator()).toHaveTextContent(/n8Tracks refused the change/);
      },
      { timeout: 4_000 },
    );
    await clock.advanceTimersAsync(2_500);
    expect(server.writes).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: 'Try again' }));
    await writesReach(server.writes, 2, 1_000);
    await waitFor(() => {
      expect(indicator()).toHaveTextContent(/^Saved$/);
    });
  }, 10_000);

  it('sends what was typed during a save after it returns, even typing back to the old text', async () => {
    const clock = fakeTimeouts();
    const { server } = versionServer([ONE]);
    await openVersion();
    let release: () => void = () => undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.next = async () => {
      await held;
      server.changeElsewhere('1', { lyrics: '[Verse]\nRun (ooh)\nX' });
      return jsonResponse(200, server.versions[0]);
    };

    typeLyrics('X');
    await writesReach(server.writes, 1);
    // Typed back to what was stored before, while the save of "X" is out.
    act(() => {
      editor().dispatch({
        changes: { from: editor().state.doc.length - 1, to: editor().state.doc.length },
        userEvent: 'delete.backward',
      });
    });
    await clock.advanceTimersAsync(2_000);
    expect(server.writes).toHaveLength(1);

    release();
    await writesReach(server.writes, 2, 1_000);
    expect(editor().state.doc.toString()).toBe('[Verse]\nRun (ooh)\n');
    expect(server.writes.map((write) => write.body)).toEqual([
      { lyrics: '[Verse]\nRun (ooh)\nX' },
      { lyrics: '[Verse]\nRun (ooh)\n' },
    ]);
    await waitFor(() => {
      expect(indicator()).toHaveTextContent(/^Saved$/);
    });
  }, 10_000);

  it('opens the shared conflict dialog when the lyrics changed elsewhere; Reload takes theirs', async () => {
    const clock = fakeTimeouts();
    const user = userEvent.setup({ advanceTimers: clock.advanceTimers });
    const { server } = versionServer([ONE]);
    await openVersion();
    server.changeElsewhere('1', { lyrics: 'Theirs' });

    typeLyrics('Mine');

    const dialog = await screen.findByRole(
      'dialog',
      { name: 'Changed since you loaded it' },
      { timeout: 4_000 },
    );
    expect(within(dialog).getByText('Theirs')).toBeInTheDocument();
    // Nothing typed is lost before the user chooses.
    expect(editor().state.doc.toString()).toBe('[Verse]\nRun (ooh)\nMine');
    await user.click(within(dialog).getByRole('button', { name: 'Reload' }));

    await waitFor(() => {
      expect(editor().state.doc.toString()).toBe('Theirs');
    });
    expect(indicator()).toHaveTextContent(/^Saved$/);
    expect(server.writes).toHaveLength(1);
  });

  it('pauses autosave on a conflict left undecided, until the user reapplies', async () => {
    const clock = fakeTimeouts();
    const user = userEvent.setup({ advanceTimers: clock.advanceTimers });
    const { server } = versionServer([ONE]);
    await openVersion();
    server.changeElsewhere('1', { lyrics: 'Theirs' });

    typeLyrics('Mine');
    const dialog = await screen.findByRole(
      'dialog',
      { name: 'Changed since you loaded it' },
      { timeout: 4_000 },
    );
    await user.click(within(dialog).getByRole('button', { name: 'Keep editing' }));

    await waitFor(() => {
      expect(indicator()).toHaveTextContent(/^Not saved: this Version was changed elsewhere/);
    });
    typeLyrics(' more');
    await clock.advanceTimersAsync(2_000);
    expect(server.writes).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: 'Reapply my change' }));
    await writesReach(server.writes, 2, 1_000);
    await waitFor(() => {
      expect(indicator()).toHaveTextContent(/^Saved$/);
    });
    expect(server.versions[0]?.lyrics).toBe('[Verse]\nRun (ooh)\nMine more');
  }, 10_000);
});

describe('leaving with changes not saved', () => {
  const TWO = testVersion('2', { lyrics: 'Two' });

  it('saves first and goes on without asking', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, TWO]);
    const { router } = await openVersion();

    typeLyrics('More');
    await user.click(screen.getByRole('treeitem', { name: /Version 2/ }));

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/songs/n8-7/v/2');
    });
    await waitFor(() => {
      expect(editor().state.doc.toString()).toBe('Two');
    });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(server.writes.map((write) => write.body)).toEqual([
      { lyrics: '[Verse]\nRun (ooh)\nMore' },
    ]);
  });

  it('asks when the save does not go through: Stay keeps the edit, Leave without saving leaves', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, TWO]);
    const { router } = await openVersion();
    const failing = () => {
      server.next = () => jsonResponse(503, { code: 'unavailable' });
    };

    failing();
    typeLyrics('More');
    act(() => {
      void router.navigate('/songs');
    });

    const dialog = await screen.findByRole('dialog', { name: 'Your changes are not saved' });
    expect(dialog).toHaveTextContent(/n8Tracks could not store the change/);
    await user.click(within(dialog).getByRole('button', { name: 'Stay' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(router.state.location.pathname).toBe('/songs/n8-7');
    expect(editor().state.doc.toString()).toBe('[Verse]\nRun (ooh)\nMore');

    failing();
    await user.click(screen.getByRole('treeitem', { name: /Version 2/ }));
    await user.click(
      within(await screen.findByRole('dialog', { name: 'Your changes are not saved' })).getByRole(
        'button',
        { name: 'Leave without saving' },
      ),
    );

    expect(await screen.findByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    await waitFor(() => {
      expect(editor().state.doc.toString()).toBe('Two');
    });
    expect(server.versions[0]?.lyrics).toBe('[Verse]\nRun (ooh)\n');
  });

  it('does not ask when nothing changed', async () => {
    const user = userEvent.setup();
    versionServer([ONE, TWO]);
    await openVersion();

    await user.click(screen.getByRole('treeitem', { name: /Version 2/ }));

    expect(await screen.findByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('asks the browser before the tab closes, and sends what is not saved as the page goes', async () => {
    const { mock } = versionServer([ONE]);
    await openVersion();

    const quiet = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(quiet);
    expect(quiet.defaultPrevented).toBe(false);

    typeLyrics('Last words');
    const ask = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(ask);
    expect(ask.defaultPrevented).toBe(true);

    window.dispatchEvent(new Event('pagehide'));
    const sent = mock.mock.calls.find(([, init]) => init?.keepalive === true);
    expect(sent?.[1]?.method).toBe('PATCH');
    const headers = new Headers(sent?.[1]?.headers);
    expect(headers.get('X-N8Tracks-Request')).toBe('1');
    expect(headers.get('If-Match')).toBe('"1"');
    const body = sent?.[1]?.body;
    expect(JSON.parse(typeof body === 'string' ? body : '')).toEqual({
      lyrics: '[Verse]\nRun (ooh)\nLast words',
    });

    // History gets the text too, after the opening text it replaced: snapshots as the page goes.
    const snapshots = mock.mock.calls
      .filter(([, init]) => init?.keepalive === true && init.method === 'POST')
      .map(([, init]) => JSON.parse(typeof init?.body === 'string' ? init.body : '') as unknown);
    expect(snapshots).toEqual([
      expect.objectContaining({ lyrics: '[Verse]\nRun (ooh)\n', styles: 'punk' }),
      expect.objectContaining({ lyrics: '[Verse]\nRun (ooh)\nLast words' }),
    ]);
  });
});
