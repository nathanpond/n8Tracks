import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { NO_LOCAL_AUDIO_FILES } from '../api/localAudioFiles';
import { renderApp } from '../test/helpers';
import {
  testGeneration,
  testSongAudioFile,
  testVersion,
  versionServer,
} from '../test/versionServer';
import { deletionAudioFileLines, movedAudioFilesLine } from './deletionAudioFileRules';

const ONE = testVersion('1', { isFrozen: true, current: true });
const G1 = 'n8-7-v1-g1';
const SUNO_ID = '0c90d621-e30c-4c76-814a-e1fdeb500582';

/** n8-7 with Version 1 holding g1 and g2; g1 has two files (one Missing), the Song one of its own. */
function songWithFiles() {
  const { server } = versionServer([ONE]);
  const first = testGeneration('1', 1, {
    audioFiles: { count: 2, missing: 1, unavailable: 0, formats: ['wav', 'mp3'] },
  });
  server.generations = [first, testGeneration('1', 2)];
  server.audioFiles = [
    testSongAudioFile(`Takes/take (suno-${SUNO_ID}).wav`, first),
    testSongAudioFile(`Takes/take (suno-${SUNO_ID}).mp3`, first, { status: 'missing' }),
    testSongAudioFile('Loose/master.mp3', null),
  ];
  return server;
}

async function openGenerationPanel(user: ReturnType<typeof userEvent.setup>, button: string) {
  renderApp(`/songs/n8-7/generations/${G1}`);
  const panel = await screen.findByRole('dialog', { name: `Generation ${G1}` });
  await waitFor(() => {
    expect(panel).toBeVisible();
  });
  await user.click(within(panel).getByRole('button', { name: button }));
}

describe('what a deletion says about local audio files', () => {
  it('says nothing when there are none', () => {
    expect(deletionAudioFileLines(NO_LOCAL_AUDIO_FILES, 'generation')).toEqual([]);
    expect(deletionAudioFileLines(NO_LOCAL_AUDIO_FILES, 'version')).toEqual([]);
    expect(deletionAudioFileLines(NO_LOCAL_AUDIO_FILES, 'song')).toEqual([]);
    expect(movedAudioFilesLine(0, 'n8-7')).toBeUndefined();
  });

  it('counts them, says they stay on disk, and what a restore will not bring back', () => {
    expect(
      deletionAudioFileLines({ total: 2, handAssociated: 0, songLevel: 0 }, 'generation'),
    ).toEqual([
      '2 local audio files are associated with it. They stay on disk and will appear in Unmatched Files.',
    ]);
    expect(
      deletionAudioFileLines({ total: 1, handAssociated: 1, songLevel: 0 }, 'version'),
    ).toEqual([
      'Its Generations have 1 local audio file. It stays on disk and will appear in Unmatched Files.',
      'It was associated by hand without a Suno ID in its name, so it will not be associated again automatically after a restore.',
    ]);
    expect(deletionAudioFileLines({ total: 5, handAssociated: 2, songLevel: 1 }, 'song')).toEqual([
      '5 local audio files are associated with it. They stay on disk and will appear in Unmatched Files.',
      '1 of them is associated with the Song itself; a restore cannot bring that association back.',
      '2 of them were associated by hand without a Suno ID in their names, so they will not be associated again automatically after a restore.',
    ]);
    expect(movedAudioFilesLine(1, 'n8-7')).toBe(
      'Its 1 local audio file and its Preferred Audio File choice move with it; the files stay where they are on disk. Files associated with n8-7 itself stay with n8-7.',
    );
  });
});

describe('the confirmations', () => {
  it('Delete Generation names its two files, Missing included, and says they stay on disk', async () => {
    const user = userEvent.setup();
    songWithFiles();
    await openGenerationPanel(user, 'Delete Generation');
    const dialog = await screen.findByRole('dialog', { name: `Delete Generation ${G1}?` });

    expect(await within(dialog).findByTestId('deletion-audio-files')).toHaveTextContent(
      '2 local audio files are associated with it. They stay on disk and will appear in Unmatched Files.',
    );
  });

  it('Delete Generation has no files line for a Generation without files', async () => {
    const user = userEvent.setup();
    const server = songWithFiles();
    server.audioFiles = [];
    await openGenerationPanel(user, 'Delete Generation');
    const dialog = await screen.findByRole('dialog', { name: `Delete Generation ${G1}?` });

    await within(dialog).findByTestId('delete-generation-summary');
    expect(within(dialog).queryByTestId('deletion-audio-files')).toBeNull();
  });

  it('Delete Version counts its Generations’ files', async () => {
    const user = userEvent.setup();
    songWithFiles();
    renderApp('/songs/n8-7/v/1');
    await screen.findByRole('tree', { name: 'Versions' });
    await waitFor(() => {
      expect(screen.queryByText('Loading the lyrics and styles…')).toBeNull();
    });
    await user.click(screen.getByRole('button', { name: 'Delete' }));
    const dialog = await screen.findByRole(
      'dialog',
      { name: 'Delete Version 1?' },
      { timeout: 5_000 },
    );

    expect(await within(dialog).findByTestId('deletion-audio-files')).toHaveTextContent(
      'Its Generations have 2 local audio files. They stay on disk and will appear in Unmatched Files.',
    );
  });

  it('Delete Song counts every file, says the Song-level one cannot be restored, and has none at zero', async () => {
    const user = userEvent.setup();
    const server = songWithFiles();
    renderApp('/songs/n8-7');
    await screen.findByRole('tree', { name: 'Versions' });
    await user.click(screen.getByRole('button', { name: 'Delete Song' }));
    let dialog = await screen.findByRole('dialog', { name: 'Delete “Running in a Pack”?' });

    const lines = await within(dialog).findByTestId('deletion-audio-files');
    expect(
      within(lines)
        .getAllByText(/./)
        .map((line) => line.textContent),
    ).toEqual([
      '3 local audio files are associated with it. They stay on disk and will appear in Unmatched Files.',
      '1 of them is associated with the Song itself; a restore cannot bring that association back.',
      '1 of them was associated by hand without a Suno ID in its name, so it will not be associated again automatically after a restore.',
    ]);
    // Never in the list of what is deleted with it.
    expect(within(dialog).getByTestId('delete-song-counts')).not.toHaveTextContent('audio');

    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Delete “Running in a Pack”?' })).toBeNull();
    });
    server.audioFiles = [];
    await user.click(screen.getByRole('button', { name: 'Delete Song' }));
    dialog = await screen.findByRole('dialog', { name: 'Delete “Running in a Pack”?' });
    await within(dialog).findByTestId('delete-song-counts');
    expect(within(dialog).queryByTestId('deletion-audio-files')).toBeNull();
  });

  it('Create new Song from Generation says its files and choice move', async () => {
    const user = userEvent.setup();
    songWithFiles();
    await openGenerationPanel(user, 'Create new Song from Generation');
    const dialog = await screen.findByRole('dialog', { name: 'Create new Song from Generation' });

    expect(within(dialog).getByTestId('move-audio-files')).toHaveTextContent(
      'Its 2 local audio files and its Preferred Audio File choice move with it; the files stay where they are on disk. Files associated with n8-7 itself stay with n8-7.',
    );
  });

  it('Create new Song from Generation has no files line for a Generation without files', async () => {
    const user = userEvent.setup();
    const server = songWithFiles();
    server.generations = [testGeneration('1', 1), testGeneration('1', 2)];
    await openGenerationPanel(user, 'Create new Song from Generation');
    const dialog = await screen.findByRole('dialog', { name: 'Create new Song from Generation' });

    expect(within(dialog).getByTestId('move-warning')).toHaveTextContent('It leaves n8-7');
    expect(within(dialog).queryByTestId('move-audio-files')).toBeNull();
  });
});
