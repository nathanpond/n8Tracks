import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { jsonResponse, renderApp } from '../test/helpers';
import {
  COMMIT_JOB_ID,
  EXPORT_ID,
  importServer,
  TARGET_SONG,
  testCommitResult,
  testImport,
  testRecord,
  toNewSong,
} from '../test/importServer';

const PAGE = `/suno/imports/${EXPORT_ID}`;

/** One record of each class: two new ones for one new Song, one linked, one ignored, one deleted, one changed. */
function everyClass() {
  return [
    toNewSong(
      testRecord('a', { title: 'Morning Light', flags: ['unknown_kind'] }),
      'new:1',
      'Morning Light',
    ),
    toNewSong(
      testRecord('b', { title: 'Morning Light (take 2)', workspaceId: 'demos' }),
      'new:1',
      'Morning Light',
    ),
    testRecord('linked', {
      title: 'Held',
      class: 'linked',
      generationId: '0199c200-0000-7000-8000-000000000001',
      generation: {
        id: '0199c200-0000-7000-8000-000000000001',
        shortcode: 'n8-7-v1-g1',
        songShortcode: 'n8-7',
      },
    }),
    testRecord('ignored', { title: 'Not wanted', class: 'ignored' }),
    testRecord('deleted', { title: 'Removed', class: 'deleted' }),
    testRecord('changed', { title: 'Renamed in Suno', class: 'changed' }),
  ];
}

function row(sunoId: string): HTMLElement {
  const found = document.querySelector<HTMLElement>(`tr[data-record="${sunoId}"]`);
  if (found === null) {
    throw new Error(`No row for ${sunoId}`);
  }
  return found;
}

async function openReview() {
  renderApp(PAGE);
  expect(await screen.findByRole('table', { name: 'Records in this import' })).toBeVisible();
}

describe('the import review page', () => {
  it('lists each class in plain words with what will happen to it', async () => {
    importServer(everyClass());
    await openReview();

    expect(within(row('a')).getByTestId('record-class')).toHaveTextContent('New');
    expect(within(row('a')).getByTestId('record-choice')).toHaveTextContent(
      'New Song “Morning Light”',
    );
    expect(within(row('a')).getByTestId('unknown-kind')).toHaveTextContent('its kind is unclear');
    expect(within(row('a')).getByRole('rowheader')).toHaveTextContent('Morning Light');
    expect(row('a')).toHaveTextContent('Studio');
    expect(row('a')).toHaveTextContent('2:05');
    expect(row('b')).toHaveTextContent('Demos');

    expect(within(row('linked')).getByTestId('record-class')).toHaveTextContent('Already linked');
    expect(within(row('linked')).getByTestId('record-choice')).toHaveTextContent(
      'Already in n8Tracks',
    );
    expect(
      within(row('linked')).getByRole('link', { name: 'Generation n8-7-v1-g1' }),
    ).toHaveAttribute('href', '/songs/n8-7/generations/n8-7-v1-g1');

    expect(within(row('ignored')).getByTestId('record-class')).toHaveTextContent('Ignored');
    expect(within(row('ignored')).getByTestId('record-choice')).toHaveTextContent('Don’t copy');
    expect(within(row('ignored')).getByTestId('record-note')).toHaveTextContent(
      'on the ignore list',
    );

    expect(within(row('deleted')).getByTestId('record-class')).toHaveTextContent(
      'Deleted in n8Tracks',
    );
    expect(within(row('deleted')).getByTestId('record-choice')).toHaveTextContent('Skip this time');
    expect(within(row('deleted')).getByTestId('record-note')).toHaveTextContent(
      'deleted in n8Tracks',
    );

    expect(within(row('changed')).getByTestId('record-class')).toHaveTextContent('Changed');
    expect(within(row('changed')).getByTestId('record-choice')).toHaveTextContent('Left as it is');
  });

  it('gives a linked or changed record no import control, and every other record a checkbox', async () => {
    importServer(everyClass());
    await openReview();

    for (const id of ['linked', 'changed']) {
      expect(within(row(id)).queryByRole('checkbox')).not.toBeInTheDocument();
    }
    for (const id of ['a', 'b', 'ignored', 'deleted']) {
      expect(within(row(id)).getByRole('checkbox')).toBeEnabled();
    }
  });

  it('lists records going to the same new Song under one heading for that target', async () => {
    importServer(everyClass());
    await openReview();

    const headings = screen.getAllByTestId('group-heading');
    expect(headings).toHaveLength(1);
    expect(headings[0]).toHaveTextContent('New Song “Morning Light” (2 records)');
    const group = headings[0]?.closest('tbody');
    expect(group).toContainElement(row('a'));
    expect(group).toContainElement(row('b'));
  });

  it('filters by class, workspace, and title through the address', async () => {
    const server = importServer(everyClass());
    const user = userEvent.setup();
    const { router } = renderApp(PAGE);
    await screen.findByRole('table', { name: 'Records in this import' });

    await user.selectOptions(screen.getByRole('combobox', { name: 'Class' }), 'deleted');
    await waitFor(() => {
      expect(screen.queryByText('Morning Light')).not.toBeInTheDocument();
    });
    expect(row('deleted')).toBeVisible();
    expect(router.state.location.search).toBe('?class=deleted');
    expect(server.recordQueries.at(-1)).toContain('class=deleted');

    await user.selectOptions(screen.getByRole('combobox', { name: 'Class' }), '');
    await user.selectOptions(screen.getByRole('combobox', { name: 'Workspace' }), 'demos');
    await waitFor(() => {
      expect(document.querySelectorAll('tr[data-record]')).toHaveLength(1);
    });
    expect(row('b')).toBeVisible();

    await user.selectOptions(screen.getByRole('combobox', { name: 'Workspace' }), '');
    await user.type(screen.getByRole('textbox', { name: 'Search titles' }), 'not');
    await waitFor(() => {
      expect(document.querySelectorAll('tr[data-record]')).toHaveLength(1);
    });
    expect(row('ignored')).toBeVisible();
    expect(server.recordQueries.at(-1)).toContain('q=not');
  });

  it('sets the ticked records to Don’t copy in one change, with the export’s revision', async () => {
    const server = importServer(everyClass());
    const user = userEvent.setup();
    await openReview();

    await user.click(within(row('a')).getByRole('checkbox', { name: 'Select Morning Light' }));
    await user.click(within(row('deleted')).getByRole('checkbox'));
    expect(screen.getByTestId('selection')).toHaveTextContent('Selected: 2 records');
    await user.click(screen.getByRole('radio', { name: 'Don’t copy' }));
    await user.click(screen.getByRole('button', { name: 'Apply to 2 records' }));

    await waitFor(() => {
      expect(within(row('a')).getByTestId('record-choice')).toHaveTextContent('Don’t copy');
    });
    expect(server.patches).toEqual([
      { ifMatch: '"1"', body: { sunoIds: ['a', 'deleted'], choice: { action: 'ignore' } } },
    ]);
    expect(screen.getByTestId('change-message')).toHaveTextContent('The choice was saved.');
    expect(screen.getByTestId('summary-ignored')).toHaveTextContent('Not copy 3 records');
  });

  it('selects a whole workspace or playlist by sending the filter, not the IDs', async () => {
    const server = importServer(everyClass());
    server.playlists = [{ id: 'favourites', name: 'Favourites', count: 1 }];
    server.records = server.records.map((record) =>
      record.sunoId === 'deleted' ? { ...record, playlistIds: ['favourites'] } : record,
    );
    const user = userEvent.setup();
    await openReview();

    await user.selectOptions(
      screen.getByRole('combobox', { name: 'Select a workspace’s records' }),
      'studio',
    );
    expect(screen.getByTestId('selection')).toHaveTextContent('every record in workspace Studio');
    expect(within(row('a')).getByRole('checkbox')).toBeChecked();
    expect(within(row('b')).getByRole('checkbox')).not.toBeChecked();
    await user.click(within(row('ignored')).getByRole('checkbox'));
    expect(screen.getByTestId('selection')).toHaveTextContent('but 1');
    await user.click(screen.getByRole('radio', { name: 'Skip this time' }));
    await user.click(
      screen.getByRole('button', { name: /^Apply to every record in workspace Studio/ }),
    );
    await waitFor(() => {
      expect(server.patches).toHaveLength(1);
    });
    expect(server.patches[0]?.body).toEqual({
      filter: { class: null, workspace: 'studio', playlist: null, q: null },
      except: ['ignored'],
      choice: { action: 'skip' },
    });

    await waitFor(() => {
      expect(screen.getByRole('combobox', { name: 'Select a playlist’s records' })).toBeEnabled();
    });
    await user.selectOptions(
      screen.getByRole('combobox', { name: 'Select a playlist’s records' }),
      'favourites',
    );
    expect(screen.getByTestId('selection')).toHaveTextContent(
      'every record in playlist Favourites',
    );
    expect(within(row('deleted')).getByRole('checkbox')).toBeChecked();

    await user.click(screen.getByRole('button', { name: 'Select all that match' }));
    expect(screen.getByTestId('selection')).toHaveTextContent(
      'every record that matches the filter',
    );
  });

  it('imports to a Version the server says matches, or to a new Version with a valid number', async () => {
    const server = importServer(everyClass());
    const user = userEvent.setup();
    await openReview();

    await user.click(within(row('b')).getByRole('checkbox'));
    await user.click(screen.getByRole('radio', { name: 'An existing Song' }));
    await user.type(screen.getByRole('textbox', { name: 'Find the Song' }), 'Tar');
    await user.click(await screen.findByRole('option', { name: /Target/ }));
    expect(screen.getByTestId('chosen-song')).toHaveTextContent('n8-7 “Target”');
    await user.click(
      await screen.findByRole('radio', {
        name: 'Version n8-7-v2, which holds the same creation inputs',
      }),
    );
    await user.click(screen.getByRole('button', { name: 'Apply to 1 record' }));
    await waitFor(() => {
      expect(server.patches).toHaveLength(1);
    });
    expect(server.patches[0]?.body.choice).toEqual({
      action: 'import',
      target: { kind: 'version', version: '0199c100-0000-7000-8000-0000000000a2' },
    });
    expect(server.targetQueries[0]).toBe('?song=n8-7');
    await waitFor(() => {
      expect(within(row('b')).getByTestId('record-choice')).toHaveTextContent(
        'Version n8-7-v2 of n8-7 “Target”',
      );
    });

    // A new Version branching from Version 2: the numbers are the server's, the proposal first.
    await user.click(within(row('deleted')).getByRole('checkbox'));
    expect(screen.getByRole('radio', { name: 'Reimport' })).toBeChecked();
    await user.click(screen.getByRole('radio', { name: 'An existing Song' }));
    await user.type(screen.getByRole('textbox', { name: 'Find the Song' }), 'Tar');
    await user.click(await screen.findByRole('option', { name: /Target/ }));
    await user.click(await screen.findByRole('radio', { name: 'A new Version' }));
    await user.selectOptions(
      screen.getByRole('combobox', { name: 'Branch from' }),
      'Version n8-7-v2',
    );
    await waitFor(() => {
      expect(screen.getByRole('combobox', { name: 'Version number' })).toHaveValue('2.1');
    });
    expect(
      within(screen.getByRole('combobox', { name: 'Version number' }))
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(['2.1 (proposed)', '3']);
    await user.selectOptions(screen.getByRole('combobox', { name: 'Version number' }), '3');
    await user.click(screen.getByRole('button', { name: 'Apply to 1 record' }));
    await waitFor(() => {
      expect(server.patches).toHaveLength(2);
    });
    expect(server.patches[1]?.body.choice).toEqual({
      action: 'import',
      target: {
        kind: 'newVersion',
        key: 'new:90',
        song: TARGET_SONG.id,
        parentVersion: '0199c100-0000-7000-8000-0000000000a2',
        number: '3',
      },
    });
    await waitFor(() => {
      expect(within(row('deleted')).getByTestId('record-choice')).toHaveTextContent(
        'Reimport: New Version 3 of n8-7',
      );
    });
  });

  it('imports to a new Song titled by the user', async () => {
    const server = importServer(everyClass());
    const user = userEvent.setup();
    await openReview();

    await user.click(within(row('ignored')).getByRole('checkbox'));
    const title = screen.getByRole('textbox', { name: 'New Song title' });
    expect(title).toHaveValue('Not wanted');
    await user.clear(title);
    expect(screen.getByRole('button', { name: 'Apply to 1 record' })).toBeDisabled();
    await user.type(title, 'Wanted after all');
    await user.click(screen.getByRole('button', { name: 'Apply to 1 record' }));
    await waitFor(() => {
      expect(server.patches).toHaveLength(1);
    });
    expect(server.patches[0]?.body.choice).toEqual({
      action: 'import',
      target: { kind: 'newSong', key: 'new:90', title: 'Wanted after all', workspaceId: 'studio' },
    });
  });

  it('shows why the server refused a change on each refused row, and keeps the choices', async () => {
    const server = importServer(everyClass());
    server.nextPatch = () =>
      jsonResponse(422, {
        code: 'invalid_choices',
        records: { a: ['inputs_differ'] },
      });
    const user = userEvent.setup();
    await openReview();

    await user.click(within(row('a')).getByRole('checkbox'));
    await user.click(screen.getByRole('button', { name: 'Apply to 1 record' }));

    expect(await within(row('a')).findByTestId('record-refused')).toHaveTextContent(
      'Not changed: its creation inputs differ',
    );
    expect(screen.getByTestId('change-message')).toHaveTextContent(
      'Nothing was changed: 1 record cannot take this choice',
    );
    expect(within(row('a')).getByTestId('record-choice')).toHaveTextContent(
      'New Song “Morning Light”',
    );
  });

  it('marks a stored choice that is no longer valid with its reason', async () => {
    const server = importServer(everyClass());
    server.invalid = { b: ['invalid_number'] };
    await openReview();

    expect(within(row('b')).getByTestId('record-invalid')).toHaveTextContent(
      'Cannot be imported as chosen: that Version number is taken',
    );
    expect(screen.getByTestId('choices-valid')).toHaveTextContent(
      '1 record cannot be imported as chosen',
    );
  });

  it('states what confirming will do, with Confirm disabled until the commit arrives', async () => {
    importServer(everyClass(), { libraryExcluded: ['disliked', 'stem', 'stemComplement'] });
    await openReview();

    expect(screen.getByTestId('summary-creates')).toHaveTextContent(
      'Create 1 Song, 1 Version, and 2 Generations.',
    );
    expect(screen.getByTestId('summary-ignored')).toHaveTextContent('Not copy 1 record');
    expect(screen.getByTestId('summary-skipped')).toHaveTextContent(
      'Leave 3 records for a later sync',
    );
    expect(screen.getByTestId('choices-valid')).toHaveTextContent('Every choice is valid.');
    const confirm = screen.getByRole('button', { name: 'Confirm import' });
    expect(confirm).toBeEnabled();
    expect(confirm).toHaveAccessibleDescription(/You are asked once more first/);
    expect(screen.getByTestId('library-excluded')).toHaveTextContent(
      'Suno’s library filters left out disliked songs, stems: they are not in this import.',
    );
  });

  it('says there is nothing to do when no record is imported or newly ignored', async () => {
    importServer([testRecord('a'), testRecord('kept', { class: 'ignored' })]);
    await openReview();

    expect(screen.getByRole('button', { name: 'Confirm import' })).toHaveAccessibleDescription(
      'There is nothing to do: no record is set to be imported or added to the ignore list.',
    );
  });

  it('offers a reload when the import changed elsewhere, and drops the unsaved choice', async () => {
    const server = importServer(everyClass());
    const user = userEvent.setup();
    await openReview();

    server.changeElsewhere();
    await user.click(within(row('a')).getByRole('checkbox'));
    await user.click(screen.getByRole('radio', { name: 'Skip this time' }));
    await user.click(screen.getByRole('button', { name: 'Apply to 1 record' }));

    expect(await screen.findByText('This import changed elsewhere')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Reload' }));
    await waitFor(() => {
      expect(screen.queryByText('This import changed elsewhere')).not.toBeInTheDocument();
    });
    expect(screen.getByTestId('selection')).toHaveTextContent('No record is selected');
    expect(within(row('a')).getByTestId('record-choice')).toHaveTextContent(
      'New Song “Morning Light”',
    );
  });

  it('discards the import after a confirmation, and then shows that state only', async () => {
    const server = importServer(everyClass());
    const user = userEvent.setup();
    await openReview();

    await user.click(screen.getByRole('button', { name: 'Discard import' }));
    const dialog = await screen.findByRole('dialog', { name: 'Discard this import?' });
    expect(within(dialog).getByTestId('discard-summary')).toHaveTextContent(
      'Nothing in your catalog changes',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Keep reviewing' }));
    expect(server.discards).toBe(0);

    await user.click(screen.getByRole('button', { name: 'Discard import' }));
    await user.click(
      within(await screen.findByRole('dialog', { name: 'Discard this import?' })).getByRole(
        'button',
        {
          name: 'Discard',
        },
      ),
    );

    expect(await screen.findByRole('heading', { name: 'This import was discarded' })).toBeVisible();
    expect(server.discards).toBe(1);
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it.each([
    ['expired', 'This import expired'],
    ['discarded', 'This import was discarded'],
    ['committed', 'This import was confirmed'],
    ['classifying', 'This import is being prepared'],
  ] as const)(
    'shows an export that is %s as that state and nothing else',
    async (state, heading) => {
      importServer(everyClass(), { state });
      renderApp(PAGE);

      expect(await screen.findByRole('heading', { name: heading })).toBeVisible();
      expect(screen.getByTestId('import-state')).toHaveAttribute('data-state', state);
      expect(screen.queryByRole('table')).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Discard import' })).not.toBeInTheDocument();
    },
  );
});

describe('confirming an import (#140)', () => {
  it('asks once more with the same numbers, then follows the job to what it created', async () => {
    const server = importServer(everyClass());
    const user = userEvent.setup();
    await openReview();

    await user.click(screen.getByRole('button', { name: 'Confirm import' }));
    const asked = await screen.findByRole('dialog', { name: 'Confirm this import?' });
    expect(within(asked).getByTestId('confirm-summary')).toHaveTextContent(
      'Create 1 Song, 1 Version, and 2 Generations.',
    );
    expect(within(asked).getByTestId('confirm-summary')).toHaveTextContent(
      'Leave 3 records for a later sync',
    );
    await user.click(within(asked).getByRole('button', { name: 'Keep reviewing' }));
    expect(server.commits).toHaveLength(0);
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: 'Confirm import' }));
    await user.click(
      within(await screen.findByRole('dialog', { name: 'Confirm this import?' })).getByRole(
        'button',
        { name: 'Confirm import' },
      ),
    );

    expect(
      await screen.findByRole('heading', { name: 'This import is being confirmed' }),
    ).toBeVisible();
    expect(server.commits).toEqual(['"1"']);
    expect(screen.getByRole('progressbar', { name: 'Import progress' })).toBeInTheDocument();
    expect(await screen.findByTestId('import-progress-text')).toHaveTextContent('1 of 2 targets');
    expect(screen.getByText(/You can leave this page/)).toBeVisible();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();

    server.finishCommit(testCommitResult());
    expect(
      await screen.findByRole('heading', { name: 'This import was confirmed' }, { timeout: 4000 }),
    ).toBeVisible();
    expect(screen.getByTestId('commit-created')).toHaveTextContent(
      'Created 1 Song, 1 Version, and 2 Generations.',
    );
    expect(screen.getByTestId('commit-outcomes')).toHaveTextContent(
      '2 records imported, 1 record left for a later sync, 1 record failed.',
    );
    expect(screen.getByRole('link', { name: 'n8-9 “Morning Light”' })).toHaveAttribute(
      'href',
      expect.stringContaining('/songs/n8-9') as string,
    );
    const notes = screen.getByTestId('commit-notes');
    expect(within(notes).getByText(/no longer match that Version/)).toBeVisible();
    expect(within(notes).queryByText('a')).not.toBeInTheDocument();
  });

  it('keeps Confirm disabled while a choice is invalid', async () => {
    const server = importServer(everyClass());
    server.invalid = { a: ['inputs_differ'] };
    await openReview();

    const confirm = screen.getByRole('button', { name: 'Confirm import' });
    expect(confirm).toBeDisabled();
    expect(confirm).toHaveAccessibleDescription('Change the marked choices first.');
  });

  it('offers a reload instead when the choices changed before the confirmation', async () => {
    const server = importServer(everyClass());
    const user = userEvent.setup();
    await openReview();

    server.changeElsewhere();
    await user.click(screen.getByRole('button', { name: 'Confirm import' }));
    await user.click(
      within(await screen.findByRole('dialog', { name: 'Confirm this import?' })).getByRole(
        'button',
        { name: 'Confirm import' },
      ),
    );

    expect(await screen.findByText('This import changed elsewhere')).toBeVisible();
    expect(server.export.state).toBe('ready');
  });

  it('shows what a confirmed import did when it is opened again later', async () => {
    const server = importServer(everyClass(), { state: 'committed', jobId: COMMIT_JOB_ID });
    server.finishCommit(testCommitResult({ records: [], songs: [] }));
    renderApp(PAGE);

    expect(await screen.findByRole('heading', { name: 'This import was confirmed' })).toBeVisible();
    expect(await screen.findByTestId('commit-created')).toHaveTextContent('Created 1 Song');
    expect(screen.queryByRole('button', { name: 'Confirm import' })).not.toBeInTheDocument();
  });

  it('goes back to the review when the import stopped part way', async () => {
    const server = importServer(everyClass(), { state: 'committing', jobId: COMMIT_JOB_ID });
    server.job = {
      id: COMMIT_JOB_ID,
      status: 'running',
      progress: 40,
      message: '1 of 2 targets',
      error: null,
      result: null,
    };
    renderApp(PAGE);

    expect(
      await screen.findByRole('heading', { name: 'This import is being confirmed' }),
    ).toBeVisible();
    server.finishCommit(null);
    expect(
      await screen.findByRole('button', { name: 'Confirm import' }, { timeout: 4000 }),
    ).toBeVisible();
  });
});

describe('the Suno import entry', () => {
  it('opens the export waiting for review', async () => {
    importServer(everyClass());
    const { router } = renderApp('/songs');
    const user = userEvent.setup();

    await user.click(
      within(await screen.findByRole('navigation', { name: 'Main' })).getByRole('link', {
        name: 'Suno import',
      }),
    );

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Review Suno import' }),
    ).toBeVisible();
    expect(router.state.location.pathname).toBe(PAGE);
  });

  it('explains how to start a sync when none waits, and that the last one was discarded', async () => {
    const server = importServer([]);
    server.current = {
      waiting: null,
      last: testImport({ state: 'discarded', capturedAt: '2026-10-06T12:00:00Z' }),
    };
    renderApp('/suno/imports');

    expect(await screen.findByTestId('no-import-waiting')).toHaveTextContent(
      'No import is waiting for review.',
    );
    expect(screen.getByTestId('last-import')).toHaveTextContent(
      'was discarded. Nothing in your catalog changed.',
    );
    expect(screen.getByText(/choose Sync/)).toBeVisible();
  });

  it('says so in Settings → Suno, with a link to the waiting import', async () => {
    importServer(everyClass());
    renderApp('/settings/suno');

    const waiting = await screen.findByTestId('import-waiting');
    expect(waiting).toHaveTextContent('An import of 6 records is waiting for review.');
    expect(within(waiting).getByRole('link', { name: 'Review the import' })).toHaveAttribute(
      'href',
      PAGE,
    );
  });

  it('says in Settings → Suno when none is waiting', async () => {
    const server = importServer([]);
    server.current = { waiting: null, last: null };
    renderApp('/settings/suno');

    expect(await screen.findByTestId('import-waiting')).toHaveTextContent(
      'No import is waiting for review.',
    );
  });
});
