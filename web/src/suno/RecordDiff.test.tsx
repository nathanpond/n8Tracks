import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { RecordDiff } from '../api/sunoImports';
import { renderApp } from '../test/helpers';
import { EXPORT_ID, importServer, testRecord } from '../test/importServer';

const PAGE = `/suno/imports/${EXPORT_ID}`;
const GENERATION = '0199c200-0000-7000-8000-000000000009';

function changedDiff(): RecordDiff {
  return {
    sunoId: 'changed',
    class: 'changed',
    generationId: GENERATION,
    fields: [
      { field: 'title', current: 'Old title', incoming: 'New title' },
      { field: 'tags', current: 'dream pop', incoming: 'shoegaze' },
      { field: 'duration', current: 125, incoming: null },
    ],
    inputs: [],
  };
}

function conflictDiff(): RecordDiff {
  return {
    sunoId: 'conflict',
    class: 'conflict',
    generationId: GENERATION,
    fields: [{ field: 'title', current: 'Kept title', incoming: 'Suno title' }],
    inputs: [{ field: 'lyrics', current: 'First words', incoming: 'Other words' }],
  };
}

function records() {
  return [
    testRecord('changed', {
      title: 'Renamed in Suno',
      class: 'changed',
      generationId: GENERATION,
      changedFields: ['title', 'tags', 'duration'],
    }),
    testRecord('conflict', {
      title: 'Made again',
      class: 'conflict',
      generationId: GENERATION,
      changedFields: ['title'],
    }),
    testRecord('new-one', { title: 'Fresh' }),
  ];
}

function row(sunoId: string): HTMLElement {
  const found = document.querySelector<HTMLElement>(`tr[data-record="${sunoId}"]`);
  if (found === null) {
    throw new Error(`No row for ${sunoId}`);
  }
  return found;
}

async function openDiff(title: string) {
  renderApp(PAGE);
  expect(await screen.findByRole('table', { name: 'Records in this import' })).toBeVisible();
  await userEvent.click(
    screen.getByRole('button', { name: `Review the differences for ${title}` }),
  );
  return screen.findByRole('dialog', { name: `Differences for “${title}”` });
}

describe('the diff of a Changed or Conflict record', () => {
  it('offers the diff only on Changed and Conflict rows, which say what to do', async () => {
    importServer(records());
    renderApp(PAGE);
    expect(await screen.findByRole('table', { name: 'Records in this import' })).toBeVisible();

    expect(within(row('changed')).getByTestId('record-note')).toHaveTextContent(
      'Review the differences',
    );
    expect(within(row('changed')).getByTestId('record-choice')).toHaveTextContent('Left as it is');
    expect(within(row('conflict')).getByTestId('record-note')).toHaveTextContent(
      'other inputs than its Version',
    );
    expect(
      within(row('new-one')).queryByRole('button', { name: /Review the differences/ }),
    ).toBeNull();
  });

  it('shows each differing field side by side, in words', async () => {
    const server = importServer(records());
    server.diffs.changed = changedDiff();
    const dialog = await openDiff('Renamed in Suno');

    const table = await within(dialog).findByRole('table', { name: 'Fields that differ' });
    const title = within(table).getByRole('rowheader', { name: 'Title' }).closest('tr');
    expect(title).toHaveTextContent('Old title');
    expect(title).toHaveTextContent('New title');
    const length = within(table).getByRole('rowheader', { name: 'Length' }).closest('tr');
    expect(length).toHaveTextContent('2:05');
    expect(length).toHaveTextContent('None');
    // Readable without colour: every row says Take or Keep, and the summary says what is taken.
    expect(within(table).getAllByText('Keep')).toHaveLength(3);
    expect(within(dialog).getByTestId('diff-summary')).toHaveTextContent(
      'Nothing is taken from Suno. Its rating, comments, and state are never changed.',
    );
    expect(within(dialog).queryByTestId('diff-inputs')).toBeNull();
  });

  it('saves the fields taken, and the rest are declined', async () => {
    const server = importServer(records());
    server.diffs.changed = changedDiff();
    const dialog = await openDiff('Renamed in Suno');

    await userEvent.click(
      await within(dialog).findByRole('checkbox', { name: 'Take Suno’s Title' }),
    );
    expect(within(dialog).getByTestId('diff-summary')).toHaveTextContent('Taken from Suno: Title.');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save choice' }));

    await waitFor(() => {
      expect(server.patches).toHaveLength(1);
    });
    expect(server.patches[0]?.body).toEqual({
      sunoIds: ['changed'],
      choice: { action: 'apply', acceptFields: ['title'] },
    });
    await waitFor(() => {
      expect(within(row('changed')).getByTestId('record-choice')).toHaveTextContent(
        'Take Title from Suno',
      );
    });
    expect(screen.queryByRole('dialog')).toBeNull();
    // A decided diff is something to confirm.
    expect(await screen.findByTestId('summary-resolved')).toHaveTextContent('Settle 1 record');
    expect(screen.getByRole('button', { name: 'Confirm import' })).toBeEnabled();
  });

  it('takes or keeps every field at once', async () => {
    const server = importServer(records());
    server.diffs.changed = changedDiff();
    const dialog = await openDiff('Renamed in Suno');

    await userEvent.click(
      await within(dialog).findByRole('button', { name: 'Take all from Suno' }),
    );
    expect(within(dialog).getByTestId('diff-summary')).toHaveTextContent(
      'Taken from Suno: Title, Style tags, Length.',
    );
    await userEvent.click(within(dialog).getByRole('button', { name: 'Keep all as they are' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save choice' }));

    await waitFor(() => {
      expect(server.patches[0]?.body.choice).toEqual({ action: 'apply', acceptFields: [] });
    });
    await waitFor(() => {
      expect(within(row('changed')).getByTestId('record-choice')).toHaveTextContent(
        'Keep n8Tracks’ data',
      );
    });
  });

  it('lists a Conflict’s differing inputs and saves moving it to a new Version', async () => {
    const server = importServer(records());
    server.diffs.conflict = conflictDiff();
    const dialog = await openDiff('Made again');

    const inputs = await within(dialog).findByRole('table', {
      name: 'Creation inputs that differ',
    });
    const lyrics = within(inputs).getByRole('rowheader', { name: 'lyrics' }).closest('tr');
    expect(lyrics).toHaveTextContent('First words');
    expect(lyrics).toHaveTextContent('Other words');
    expect(within(dialog).getByText(/A Version’s inputs are never changed/)).toBeVisible();
    // Decide later is the default, and takes no field.
    expect(
      within(dialog).getByRole('radio', { name: 'Decide later (leave it as it is)' }),
    ).toBeChecked();
    expect(within(dialog).getByRole('checkbox', { name: 'Take Suno’s Title' })).toBeDisabled();

    await userEvent.click(
      within(dialog).getByRole('radio', { name: 'Move it to a new Version holding Suno’s inputs' }),
    );
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save choice' }));

    await waitFor(() => {
      expect(server.patches[0]?.body.choice).toEqual({
        action: 'moveToNewVersion',
        acceptFields: [],
      });
    });
    await waitFor(() => {
      expect(within(row('conflict')).getByTestId('record-choice')).toHaveTextContent(
        'Move to a new Version',
      );
    });
  });

  it('keeps a Conflict where it is with a field taken, or leaves it for later', async () => {
    const server = importServer(records());
    server.diffs.conflict = conflictDiff();
    let dialog = await openDiff('Made again');

    await userEvent.click(
      await within(dialog).findByRole('radio', { name: 'Keep it where it is' }),
    );
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Take Suno’s Title' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save choice' }));
    await waitFor(() => {
      expect(server.patches[0]?.body.choice).toEqual({ action: 'keep', acceptFields: ['title'] });
    });
    await waitFor(() => {
      expect(within(row('conflict')).getByTestId('record-choice')).toHaveTextContent(
        'Keep it where it is, and take Title from Suno',
      );
    });

    await userEvent.click(
      screen.getByRole('button', { name: 'Review the differences for Made again' }),
    );
    dialog = await screen.findByRole('dialog', { name: 'Differences for “Made again”' });
    expect(await within(dialog).findByRole('radio', { name: 'Keep it where it is' })).toBeChecked();
    await userEvent.click(
      within(dialog).getByRole('radio', { name: 'Decide later (leave it as it is)' }),
    );
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save choice' }));
    await waitFor(() => {
      expect(server.patches[1]?.body.choice).toEqual({ action: 'skip' });
    });
  });

  it('says so when the diff cannot be read', async () => {
    importServer(records());
    const dialog = await openDiff('Renamed in Suno');

    expect(await within(dialog).findByText('The differences could not be loaded')).toBeVisible();
    expect(within(dialog).getByRole('button', { name: 'Save choice' })).toBeDisabled();
  });
});
