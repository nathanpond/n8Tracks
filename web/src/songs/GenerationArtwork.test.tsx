import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../test/helpers';
import { baseSong, songServer, testArtwork, testAssetId } from '../test/songServer';
import { testGeneration } from '../test/versionServer';

const TITLE = baseSong.title;
const ALT = `Artwork for ${TITLE}`;

/** The Selected Generation's image (asset 5) and another Generation's (asset 6). */
const SELECTED_IMAGE = testArtwork(testAssetId(5), { width: 800, height: 800 });
const OTHER_IMAGE = testArtwork(testAssetId(6), { width: 800, height: 800 });

/** A Song showing its Selected Generation g1's image, with Generations g1 and g2 (images) and g3 (none). */
function defaultedSong() {
  const { server } = songServer({
    ...baseSong,
    artwork: { ...SELECTED_IMAGE, source: 'selectedGeneration' },
    hasSelectedGeneration: true,
    selectedGeneration: {
      id: 'g1',
      shortcode: 'n8-7-v1-g1',
      state: 'active',
      remoteState: 'present',
    },
  });
  server.generations = [
    testGeneration('1', 1, { isSelected: true, artwork: SELECTED_IMAGE }),
    testGeneration('1', 2, { artwork: OTHER_IMAGE }),
    testGeneration('1', 3),
  ];
  return server;
}

async function openArtwork(user: ReturnType<typeof userEvent.setup>) {
  renderApp('/songs/n8-7');
  await screen.findByRole('heading', { level: 2, name: TITLE });
  await user.click(screen.getByRole('button', { name: 'Details' }));
  return screen.findByRole('group', { name: 'Artwork' });
}

describe('a Song’s artwork from its Generations', () => {
  it('shows the Selected Generation’s image until the Song has its own, offering only to give it its own', async () => {
    const user = userEvent.setup();
    defaultedSong();
    const group = await openArtwork(user);

    expect(within(group).getByRole('img', { name: ALT })).toHaveAttribute(
      'src',
      SELECTED_IMAGE.squareUrls['320'],
    );
    expect(
      within(screen.getByTestId('song-header-artwork')).getByRole('img', { name: ALT }),
    ).toHaveAttribute('src', SELECTED_IMAGE.squareUrls['320']);
    expect(within(group).getByTestId('artwork-inherited')).toHaveTextContent(
      'Showing the image of the Selected Generation n8-7-v1-g1 until the Song has artwork of its own.',
    );
    expect(within(group).getByRole('button', { name: 'Upload artwork' })).toBeEnabled();
    expect(within(group).queryByRole('button', { name: 'Remove artwork' })).toBeNull();
    expect(within(group).queryByRole('button', { name: 'Crop artwork' })).toBeNull();
  });

  it('shows the newest Generation’s image while no Generation is selected, as an imported Song does (#318)', async () => {
    const user = userEvent.setup();
    const { server } = songServer({
      ...baseSong,
      artwork: { ...OTHER_IMAGE, source: 'newestGeneration' },
      hasSelectedGeneration: false,
      selectedGeneration: null,
    });
    server.generations = [
      testGeneration('1', 1, { artwork: SELECTED_IMAGE }),
      testGeneration('1', 2, { artwork: OTHER_IMAGE }),
    ];
    const group = await openArtwork(user);

    expect(within(group).getByRole('img', { name: ALT })).toHaveAttribute(
      'src',
      OTHER_IMAGE.squareUrls['320'],
    );
    expect(within(group).getByTestId('artwork-inherited')).toHaveTextContent(
      'Showing the image of the newest Generation that has one until a Generation is selected or the Song has artwork of its own.',
    );
    expect(within(group).queryByRole('button', { name: 'Remove artwork' })).toBeNull();
  });

  it('copies the chosen Generation’s image to the Song as its own', async () => {
    const user = userEvent.setup();
    const server = defaultedSong();
    const group = await openArtwork(user);

    await user.click(within(group).getByRole('button', { name: 'Choose from Generations' }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose a Generation’s image' });
    const list = await within(dialog).findByRole('list', { name: 'Generation images' });
    expect(
      within(list)
        .getAllByRole('button')
        .map((button) => button.getAttribute('aria-label')),
    ).toEqual(['Use the image of n8-7-v1-g1', 'Use the image of n8-7-v1-g2']);

    await user.click(within(list).getByRole('button', { name: 'Use the image of n8-7-v1-g2' }));

    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent(
        'Artwork set from Generation n8-7-v1-g2.',
      );
    });
    expect(server.picks).toEqual([{ ifMatch: '"1"', generation: server.generations?.[1]?.id }]);
    expect(within(group).getByRole('img', { name: ALT })).toHaveAttribute(
      'src',
      OTHER_IMAGE.squareUrls['320'],
    );
    expect(within(group).queryByTestId('artwork-inherited')).toBeNull();
    expect(within(group).getByRole('button', { name: 'Remove artwork' })).toBeEnabled();
    expect(within(group).getByRole('button', { name: 'Replace artwork' })).toBeEnabled();
  });

  it('sends the choice again once with the current revision when the Song changed meanwhile', async () => {
    const user = userEvent.setup();
    const server = defaultedSong();
    const group = await openArtwork(user);
    server.changeElsewhere({ notes: 'Changed in another tab' });

    await user.click(within(group).getByRole('button', { name: 'Choose from Generations' }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose a Generation’s image' });
    await user.click(
      await within(dialog).findByRole('button', { name: 'Use the image of n8-7-v1-g1' }),
    );

    await waitFor(() => {
      expect(server.picks.map((pick) => pick.ifMatch)).toEqual(['"1"', '"2"']);
    });
    await waitFor(() => {
      expect(within(group).getByTestId('artwork-status')).toHaveTextContent(
        'Artwork set from Generation n8-7-v1-g1.',
      );
    });
    expect(server.song.artwork?.source).toBe('own');
  });

  it('says so when no Generation has an image yet', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    server.generations = [testGeneration('1', 1)];
    const group = await openArtwork(user);
    expect(within(group).getByRole('img', { name: 'No artwork' })).toBeInTheDocument();
    expect(within(group).queryByTestId('artwork-inherited')).toBeNull();

    await user.click(within(group).getByRole('button', { name: 'Choose from Generations' }));
    const dialog = await screen.findByRole('dialog', { name: 'Choose a Generation’s image' });
    expect(await within(dialog).findByTestId('no-generation-images')).toHaveTextContent(
      'None of this Song’s Generations has an image yet.',
    );
    expect(server.picks).toEqual([]);
  });

  it('removing the Song’s own artwork says it goes back to the Selected Generation’s image', async () => {
    const user = userEvent.setup();
    songServer({ ...baseSong, artwork: testArtwork(testAssetId(1)) });
    const group = await openArtwork(user);

    await user.click(within(group).getByRole('button', { name: 'Remove artwork' }));

    expect(await screen.findByTestId('remove-artwork-summary')).toHaveTextContent(
      'The Song will show its Selected Generation’s image, or a placeholder when that has none, instead.',
    );
  });
});
