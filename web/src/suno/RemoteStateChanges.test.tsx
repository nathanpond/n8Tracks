import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../test/helpers';
import {
  COMMIT_JOB_ID,
  EXPORT_ID,
  importServer,
  testCommitResult,
  testRecord,
  testRemoteState,
} from '../test/importServer';

const PAGE = `/suno/imports/${EXPORT_ID}`;

/** One change of each kind: Alpha in Suno's Trash, Beta restored (sync archived it), Gamma missing, Delta restored (the user archived it). */
function everyChange() {
  return [
    testRemoteState('a', { title: 'Alpha' }),
    testRemoteState('b', {
      title: 'Beta',
      change: 'restored',
      remoteState: 'trashed',
      newRemoteState: 'present',
      state: 'archived',
      newState: 'active',
      archives: false,
      reactivates: true,
    }),
    testRemoteState('c', {
      title: 'Gamma',
      change: 'missing',
      newRemoteState: 'missing',
      newState: 'active',
      archives: false,
    }),
    testRemoteState('d', {
      title: 'Delta',
      change: 'restored',
      remoteState: 'trashed',
      newRemoteState: 'present',
      state: 'archived',
      newState: 'archived',
      archives: false,
    }),
  ];
}

function remoteRow(sunoId: string): HTMLElement {
  const found = document.querySelector<HTMLElement>(`tr[data-remote-state="${sunoId}"]`);
  if (found === null) {
    throw new Error(`No Suno state change for ${sunoId}`);
  }
  return found;
}

async function openChanges() {
  renderApp(`${PAGE}?class=remote-state`);
  expect(await screen.findByRole('table', { name: 'Suno state changes' })).toBeVisible();
}

describe('the Suno state changes of an import review (#142)', () => {
  it('is a class in the filter, with how many changes there are', async () => {
    const server = importServer([testRecord('linked', { class: 'linked' })]);
    server.remoteStates = everyChange();
    renderApp(PAGE);

    const select = await screen.findByRole('combobox', { name: 'Class' });
    expect(
      within(select).getByRole('option', { name: 'Suno state changes (4)' }),
    ).toBeInTheDocument();
    await userEvent.selectOptions(select, 'remote-state');

    expect(await screen.findByRole('table', { name: 'Suno state changes' })).toBeVisible();
    expect(screen.queryByRole('table', { name: 'Records in this import' })).toBeNull();
    expect(screen.queryByRole('group', { name: 'Select records' })).toBeNull();
  });

  it('lists each change in plain words, pre-selected to apply, linking to its Generation', async () => {
    const server = importServer([]);
    server.remoteStates = everyChange();
    await openChanges();

    expect(within(remoteRow('a')).getByTestId('remote-state-change')).toHaveTextContent(
      'In Suno Trash: will be archived',
    );
    expect(within(remoteRow('b')).getByTestId('remote-state-change')).toHaveTextContent(
      'Restored in Suno: will be reactivated',
    );
    expect(within(remoteRow('c')).getByTestId('remote-state-change')).toHaveTextContent(
      'Remote Missing',
    );
    expect(within(remoteRow('c')).getByTestId('remote-state-note')).toHaveTextContent(
      'Nothing is deleted',
    );
    expect(within(remoteRow('d')).getByTestId('remote-state-change')).toHaveTextContent(
      /^Restored in Suno$/,
    );
    expect(within(remoteRow('d')).getByTestId('remote-state-note')).toHaveTextContent(
      'You archived it yourself, so it stays archived.',
    );
    for (const title of ['Alpha', 'Beta', 'Gamma', 'Delta']) {
      expect(screen.getByRole('checkbox', { name: `Apply: ${title}` })).toBeChecked();
    }
    expect(within(remoteRow('a')).getByRole('link', { name: 'n8-7-v1-g1' })).toHaveAttribute(
      'href',
      '/songs/n8-7/generations/n8-7-v1-g1',
    );
    expect(screen.getByTestId('remote-states-summary')).toHaveTextContent(
      '1 in Suno Trash, 2 restored in Suno, 1 Remote Missing. Nothing is deleted, and nothing changes in Suno.',
    );
  });

  it('sets one change to Skip with the revision read, and the summary follows', async () => {
    const server = importServer([]);
    server.remoteStates = everyChange();
    await openChanges();
    expect(await screen.findByTestId('summary-remote')).toHaveTextContent(
      'Follow 4 Suno state changes of 4',
    );

    await userEvent.click(screen.getByRole('checkbox', { name: 'Apply: Alpha' }));

    await waitFor(() => {
      expect(screen.getByRole('checkbox', { name: 'Apply: Alpha' })).not.toBeChecked();
    });
    expect(server.remotePatches).toEqual([
      { ifMatch: '"1"', body: { sunoIds: ['a'], apply: false } },
    ]);
    expect(within(remoteRow('a')).getByTestId('remote-state-change')).toHaveTextContent(
      'Skipped: In Suno Trash: will be archived',
    );
    expect(screen.getByTestId('remote-state-message')).toHaveTextContent('skipped this time');
    await waitFor(() => {
      expect(screen.getByTestId('summary-remote')).toHaveTextContent(
        'Follow 3 Suno state changes of 4',
      );
    });

    // A second change goes at the new revision without waiting for the review to reload.
    await userEvent.click(screen.getByRole('checkbox', { name: 'Apply: Alpha' }));
    await waitFor(() => {
      expect(screen.getByRole('checkbox', { name: 'Apply: Alpha' })).toBeChecked();
    });
    expect(server.remotePatches[1]).toEqual({
      ifMatch: '"2"',
      body: { sunoIds: ['a'], apply: true },
    });
  });

  it('makes Confirm possible when the only thing to do is a Suno state change', async () => {
    const server = importServer([testRecord('linked', { class: 'linked' })]);
    server.remoteStates = [testRemoteState('a', { title: 'Alpha' })];
    renderApp(PAGE);

    expect(await screen.findByRole('button', { name: 'Confirm import' })).toBeEnabled();
  });

  it('says the export changed elsewhere when its revision moved on', async () => {
    const server = importServer([]);
    server.remoteStates = everyChange();
    await openChanges();
    server.changeElsewhere();

    await userEvent.click(screen.getByRole('checkbox', { name: 'Apply: Gamma' }));

    expect(await screen.findByText('This import changed elsewhere')).toBeVisible();
    expect(screen.getByRole('checkbox', { name: 'Apply: Gamma' })).toBeChecked();
  });

  it('says when nothing changed in Suno, and why nothing is missing after a partial sync', async () => {
    importServer([], { libraryComplete: false });
    renderApp(`${PAGE}?class=remote-state`);

    expect(await screen.findByTestId('no-remote-states')).toHaveTextContent(
      'Nothing changed in Suno for the clips already in n8Tracks.',
    );
    expect(screen.getByTestId('remote-states-summary')).toHaveTextContent(
      'did not read your whole library and Trash to the end',
    );
  });

  it('shows the Suno state changes the commit followed', async () => {
    const server = importServer([], { state: 'committed', jobId: COMMIT_JOB_ID });
    server.finishCommit(
      testCommitResult({
        remoteStates: [
          {
            sunoId: 'a',
            change: 'trashed',
            outcome: 'applied',
            generation: { id: 'g', shortcode: 'n8-7-v1-g1' },
          },
          {
            sunoId: 'c',
            change: 'missing',
            outcome: 'skipped',
            generation: { id: 'h', shortcode: 'n8-7-v1-g3' },
          },
        ],
      }),
    );
    renderApp(PAGE);

    expect(await screen.findByTestId('commit-remote-states')).toHaveTextContent(
      'Followed 1 Suno state change; skipped 1.',
    );
  });
});
