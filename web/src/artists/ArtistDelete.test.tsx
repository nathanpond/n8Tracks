import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { artistServer, testArtist } from '../test/artistServer';
import { renderApp } from '../test/helpers';

const CREDITED = testArtist('Old Name', { songCount: 2, albumCount: 1 });
const HEIR = testArtist('Heir');
const LONELY = testArtist('Lonely');

/** Opens the Artist's page and its delete confirmation; the dialog. */
async function openDialog(artist: { id: string; name: string }) {
  const user = userEvent.setup();
  renderApp(`/artists/${artist.id}`);
  await screen.findByRole('form', { name: 'Artist details' });
  await user.click(screen.getByRole('button', { name: 'Delete Artist' }));
  const dialog = await screen.findByRole('dialog', { name: `Delete “${artist.name}”?` });
  await waitFor(() => {
    expect(dialog).toBeVisible();
  });
  // It has asked whether other Artists exist and whether this is the default.
  await waitFor(() => {
    expect(within(dialog).queryByLabelText('Checking the Artist')).not.toBeInTheDocument();
  });
  return { user, dialog };
}

const deleteButton = (dialog: HTMLElement) =>
  within(dialog).getByRole('button', { name: 'Delete Artist' });

describe('deleting an Artist', () => {
  it('needs only a confirmation for an Artist nothing credits, then opens the list with a notice', async () => {
    const server = artistServer([LONELY, HEIR]);
    const { user, dialog } = await openDialog(LONELY);

    expect(within(dialog).getByTestId('delete-artist-summary')).toHaveTextContent(
      'Deleting the Artist “Lonely” is permanent. Its aliases, links, and artwork are deleted with it. No Song or Album is deleted.',
    );
    expect(within(dialog).getByTestId('delete-artist-credits')).toHaveTextContent(
      'No Song or Album credits it, so nothing else changes.',
    );
    expect(within(dialog).queryByRole('radiogroup')).not.toBeInTheDocument();
    expect(within(dialog).queryByTestId('delete-artist-default')).not.toBeInTheDocument();

    await user.click(deleteButton(dialog));

    const notice = await screen.findByTestId('collection-deleted-notice');
    expect(notice).toHaveTextContent('Deleted the Artist “Lonely”. No Song or Album was deleted.');
    expect(screen.getByRole('heading', { level: 2, name: 'Artists' })).toBeVisible();
    expect(server.deleted).toEqual([{ id: LONELY.id, query: '' }]);
    expect(server.writes).toEqual([expect.objectContaining({ method: 'DELETE', ifMatch: '"1"' })]);

    await user.click(within(notice).getByRole('button', { name: 'Dismiss' }));
    expect(screen.queryByTestId('collection-deleted-notice')).not.toBeInTheDocument();
  });

  it('states the counts and reassigns the credits to the Artist chosen', async () => {
    const server = artistServer([CREDITED, HEIR]);
    const { user, dialog } = await openDialog(CREDITED);

    expect(within(dialog).getByTestId('delete-artist-credits')).toHaveTextContent(
      'It is credited on 2 Songs and 1 Album.',
    );
    // A choice is needed first.
    expect(deleteButton(dialog)).toBeDisabled();

    await user.click(
      within(dialog).getByRole('radio', { name: 'Reassign them to another Artist' }),
    );
    expect(deleteButton(dialog)).toBeDisabled();
    await user.type(within(dialog).getByRole('textbox', { name: 'Reassign to' }), 'He');
    await user.click(await within(dialog).findByRole('option', { name: 'Heir' }));
    expect(within(dialog).getByTestId('delete-artist-target')).toHaveTextContent(
      'The credits go to “Heir”.',
    );
    // The Artist being deleted is never offered.
    await user.type(within(dialog).getByRole('textbox', { name: 'Reassign to' }), 'Old');
    expect(
      await within(dialog).findByRole('option', { name: 'Create Artist “Old”' }),
    ).toBeVisible();
    expect(within(dialog).queryByRole('option', { name: 'Old Name' })).not.toBeInTheDocument();
    await user.clear(within(dialog).getByRole('textbox', { name: 'Reassign to' }));

    await user.click(deleteButton(dialog));

    expect(await screen.findByTestId('collection-deleted-notice')).toHaveTextContent(
      'Deleted the Artist “Old Name”. Its credits went to “Heir”. No Song or Album was deleted.',
    );
    expect(server.deleted).toEqual([{ id: CREDITED.id, query: `?reassignTo=${HEIR.id}` }]);
  });

  it('removes the credits when that is chosen, saying what the Songs and Albums are left with', async () => {
    const server = artistServer([CREDITED, HEIR]);
    const { user, dialog } = await openDialog(CREDITED);

    await user.click(within(dialog).getByRole('radio', { name: 'Remove them' }));
    expect(within(dialog).getByTestId('delete-artist-removal')).toHaveTextContent(
      'Songs it is the primary Artist of are left with no primary Artist, and Albums it is the Album Artist of are left with none.',
    );
    await user.click(deleteButton(dialog));

    expect(await screen.findByTestId('collection-deleted-notice')).toHaveTextContent(
      'Its credits were removed. No Song or Album was deleted.',
    );
    expect(server.deleted).toEqual([{ id: CREDITED.id, query: '?removeCredits=true' }]);
  });

  it('offers only removal when there is no other Artist', async () => {
    const server = artistServer([CREDITED]);
    const { user, dialog } = await openDialog(CREDITED);

    expect(within(dialog).getByTestId('delete-artist-only-removal')).toHaveTextContent(
      'There is no other Artist to reassign them to, so they will be removed.',
    );
    expect(within(dialog).queryByRole('radio')).not.toBeInTheDocument();
    expect(within(dialog).getByTestId('delete-artist-removal')).toBeVisible();

    await user.click(deleteButton(dialog));

    await screen.findByTestId('collection-deleted-notice');
    expect(server.deleted).toEqual([{ id: CREDITED.id, query: '?removeCredits=true' }]);
  });

  it('says that the default Artist for new Songs will be cleared', async () => {
    const server = artistServer([LONELY, HEIR]);
    server.catalog = { revision: 3, defaultArtist: { id: LONELY.id, name: LONELY.name } };
    const { user, dialog } = await openDialog(LONELY);

    expect(within(dialog).getByTestId('delete-artist-default')).toHaveTextContent(
      'It is the default Artist for new Songs. The default will be cleared',
    );

    await user.click(deleteButton(dialog));
    await screen.findByTestId('collection-deleted-notice');
    expect(server.catalog.defaultArtist).toBeNull();
  });

  it('asks for a choice when Songs credited the Artist since the page loaded, showing the counts now', async () => {
    const server = artistServer([LONELY, HEIR]);
    const { user, dialog } = await openDialog(LONELY);

    // Crediting an Artist on a Song does not change the Artist's revision.
    server.artists = server.artists.map((artist) =>
      artist.id === LONELY.id ? { ...artist, songCount: 1 } : artist,
    );
    await user.click(deleteButton(dialog));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Not deleted: 1 Song now credits this Artist. Choose what happens to the credits, then choose Delete again.',
    );
    expect(within(dialog).getByTestId('delete-artist-credits')).toHaveTextContent(
      'It is credited on 1 Song.',
    );
    expect(deleteButton(dialog)).toBeDisabled();
    await user.click(within(dialog).getByRole('radio', { name: 'Remove them' }));
    await user.click(deleteButton(dialog));

    await screen.findByTestId('collection-deleted-notice');
    expect(server.deleted).toEqual([{ id: LONELY.id, query: '?removeCredits=true' }]);
  });

  it('deletes nothing when the Artist changed elsewhere, and says so', async () => {
    const server = artistServer([LONELY, HEIR]);
    const { user, dialog } = await openDialog(LONELY);

    server.changeElsewhere(LONELY.id, { name: 'Renamed' });
    await user.click(deleteButton(dialog));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Not deleted: the Artist was changed elsewhere since this opened.',
    );
    expect(server.deleted).toEqual([]);
    expect(screen.getByRole('heading', { level: 2, name: 'Renamed' })).toBeVisible();

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });
});
