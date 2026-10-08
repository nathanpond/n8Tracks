import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../test/helpers';
import {
  testGeneration,
  testSongAudioFile,
  testVersion,
  versionServer,
} from '../test/versionServer';
import { playsNow, preferenceNote } from './songAudioFilesRules';

/**
 * Preferred audio files (#212) on the Song page: the marks on the Audio Files section and the
 * Generation panel (preferred, plays now, plays for the Song, and why the preferred file is not the
 * one playing), Make preferred and Clear preferred for a Generation and for the Song, and the
 * association dialog's warning before a change that would clear a choice.
 */

const ONE = testVersion('1', { current: true, isFrozen: true });
const G1 = 'n8-7-v1-g1';

async function section() {
  return screen.findByRole('region', { name: 'Audio Files' });
}

function rowOf(region: HTMLElement, path: string): HTMLElement {
  const found = within(region)
    .getAllByTestId('song-audio-file')
    .find((row) => row.getAttribute('data-file') === path);
  if (found === undefined) {
    throw new Error(`${path} is not listed.`);
  }
  return found;
}

describe('the preferred file marks', () => {
  it('marks what plays now, the preferred file, and why they differ', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1);
    server.generations = [first];
    server.audioFiles = [
      testSongAudioFile('Master.wav', null, { isPreferred: true, status: 'missing' }),
      testSongAudioFile('Take.wav', first, { playsForGeneration: true, playsForSong: true }),
      testSongAudioFile('Take.mp3', first, { isPreferred: true, status: 'missing' }),
    ];

    renderApp('/songs/n8-7');
    const region = await section();
    await within(region).findAllByTestId('song-audio-file');

    const master = rowOf(region, 'Master.wav');
    expect(within(master).getByTestId('audio-file-preferred')).toHaveTextContent('Preferred');
    expect(within(master).queryByTestId('audio-file-plays-now')).toBeNull();
    expect(within(master).getByTestId('audio-file-preference-note')).toHaveTextContent(
      'Preferred for the Song, but Missing: Take.wav (n8-7-v1-g1) plays instead, from the Selected Generation.',
    );
    expect(master).toHaveAttribute('data-preferred', 'true');
    expect(master).toHaveAttribute('data-plays-now', 'false');

    const wav = rowOf(region, 'Take.wav');
    expect(within(wav).getByTestId('audio-file-plays-now')).toHaveTextContent('Plays now');
    expect(within(wav).getByTestId('audio-file-plays-for-song')).toHaveTextContent(
      'Plays for the Song',
    );
    expect(within(wav).queryByTestId('audio-file-preferred')).toBeNull();
    expect(wav).toHaveAttribute('data-plays-now', 'true');

    const mp3 = rowOf(region, 'Take.mp3');
    expect(within(mp3).getByTestId('audio-file-preference-note')).toHaveTextContent(
      'Preferred, but Missing: Take.wav plays instead.',
    );
    expect(
      within(mp3).getByRole('button', {
        name: `Clear preferred file of Generation ${G1}: Take.mp3`,
      }),
    ).toBeEnabled();
    expect(
      within(wav).getByRole('button', { name: `Make preferred for Generation ${G1}: Take.wav` }),
    ).toBeEnabled();
    expect(
      within(master).getByRole('button', { name: 'Clear preferred file of the Song: Master.wav' }),
    ).toBeEnabled();
  });

  it('shows the same marks in the Generation panel', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1);
    server.generations = [first];
    server.audioFiles = [
      testSongAudioFile('Take.wav', first),
      testSongAudioFile('Take.mp3', first, { isPreferred: true, playsForGeneration: true }),
    ];

    renderApp(`/songs/n8-7/generations/${G1}`);
    const panel = await screen.findByRole('dialog', { name: `Generation ${G1}` });
    const region = within(panel).getByRole('region', { name: 'Local audio files' });
    const items = await within(region).findAllByTestId('generation-audio-file');

    const mp3 = items.find((item) => item.getAttribute('data-file') === 'Take.mp3') ?? region;
    expect(mp3).toHaveAttribute('data-preferred', 'true');
    expect(mp3).toHaveAttribute('data-plays-now', 'true');
    expect(within(mp3).getByTestId('audio-file-preferred')).toBeInTheDocument();
    expect(within(mp3).getByTestId('audio-file-plays-now')).toBeInTheDocument();
    expect(within(mp3).queryByTestId('audio-file-preference-note')).toBeNull();
  });
});

describe('choosing a preferred file', () => {
  it('makes a Generation’s file preferred under the Generation’s revision and reads the lists again', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1, { revision: 6 });
    server.generations = [first];
    server.audioFiles = [
      testSongAudioFile('Take.wav', first, { playsForGeneration: true }),
      testSongAudioFile('Take.mp3', first),
    ];
    const user = userEvent.setup();

    renderApp('/songs/n8-7');
    const region = await section();
    await within(region).findAllByTestId('song-audio-file');
    const generationReads = server.generationReads;

    await user.click(
      within(region).getByRole('button', { name: `Make preferred for Generation ${G1}: Take.mp3` }),
    );

    await waitFor(() => {
      expect(within(region).getByTestId('audio-files-announcement')).toHaveTextContent(
        `Take.mp3 is now the preferred file of Generation ${G1}.`,
      );
    });
    const mp3 = server.audioFiles.find((file) => file.fileName === 'Take.mp3');
    expect(server.preferenceWrites).toEqual([
      {
        path: `/api/v1/generations/${first.id}/preferred-audio-file`,
        method: 'PUT',
        ifMatch: '"6"',
        body: { audioFile: mp3?.id },
      },
    ]);
    await waitFor(() => {
      expect(rowOf(region, 'Take.mp3')).toHaveAttribute('data-preferred', 'true');
    });
    expect(rowOf(region, 'Take.mp3')).toHaveAttribute('data-plays-now', 'true');
    expect(rowOf(region, 'Take.wav')).toHaveAttribute('data-plays-now', 'false');
    expect(server.generationReads).toBeGreaterThan(generationReads);

    // Cleared: the automatic choice plays again.
    await user.click(
      within(region).getByRole('button', {
        name: `Clear preferred file of Generation ${G1}: Take.mp3`,
      }),
    );
    await waitFor(() => {
      expect(within(region).getByTestId('audio-files-announcement')).toHaveTextContent(
        `Cleared the preferred file of Generation ${G1}: the automatic choice plays.`,
      );
    });
    expect(server.preferenceWrites[1]).toMatchObject({ method: 'DELETE', ifMatch: '"7"' });
    await waitFor(() => {
      expect(rowOf(region, 'Take.wav')).toHaveAttribute('data-plays-now', 'true');
    });
  });

  it('makes a Song-level file the Song’s preferred file under the Song’s revision', async () => {
    const { server } = versionServer([ONE]);
    server.audioFiles = [testSongAudioFile('Master.wav', null)];
    const revision = server.song.revision;
    const user = userEvent.setup();

    renderApp('/songs/n8-7');
    const region = await section();
    await within(region).findAllByTestId('song-audio-file');

    await user.click(
      within(region).getByRole('button', { name: 'Make preferred for the Song: Master.wav' }),
    );

    await waitFor(() => {
      expect(within(region).getByTestId('audio-files-announcement')).toHaveTextContent(
        'Master.wav is now the preferred file of the Song.',
      );
    });
    expect(server.preferenceWrites).toEqual([
      {
        path: `/api/v1/songs/${server.song.id}/preferred-audio-file`,
        method: 'PUT',
        ifMatch: `"${String(revision)}"`,
        body: { audioFile: server.audioFiles[0]?.id },
      },
    ]);
    await waitFor(() => {
      expect(rowOf(region, 'Master.wav')).toHaveAttribute('data-plays-now', 'true');
    });
  });

  it('says a stale choice changed nothing', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1, { revision: 6 });
    server.generations = [first];
    server.audioFiles = [testSongAudioFile('Take.mp3', first)];
    const user = userEvent.setup();

    renderApp('/songs/n8-7');
    const region = await section();
    await within(region).findAllByTestId('song-audio-file');
    server.generations = [{ ...first, revision: 9 }];

    await user.click(
      within(region).getByRole('button', { name: `Make preferred for Generation ${G1}: Take.mp3` }),
    );

    expect(await within(region).findByRole('alert')).toHaveTextContent(
      `Generation ${G1} changed since the page was loaded, so its preferred file was not changed.`,
    );
    expect(rowOf(region, 'Take.mp3')).toHaveAttribute('data-preferred', 'false');
  });
});

describe('changing a preferred file’s association', () => {
  it('opens the association dialog, which warns first, instead of removing at once', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1);
    server.generations = [first];
    server.audioFiles = [
      testSongAudioFile('Take.mp3', first, { isPreferred: true, playsForGeneration: true }),
    ];
    const user = userEvent.setup();

    renderApp('/songs/n8-7');
    const region = await section();
    await within(region).findAllByTestId('song-audio-file');

    await user.click(within(region).getByRole('button', { name: 'Remove association: Take.mp3' }));

    const dialog = await screen.findByRole('dialog', { name: 'Change association' });
    expect(within(dialog).getByTestId('preference-warning')).toHaveTextContent(
      `It is the preferred file of Generation ${G1}. Changing or removing its association clears that choice, and the automatic choice plays instead.`,
    );
    expect(server.associationWrites).toEqual([]);

    // Removing from the dialog is the confirmation.
    await user.click(within(dialog).getByRole('button', { name: 'Remove association' }));
    await waitFor(() => {
      expect(server.associationWrites).toHaveLength(1);
    });
  });

  it('removes a file that is not preferred at once, as before', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1);
    server.generations = [first];
    server.audioFiles = [testSongAudioFile('Take.mp3', first)];
    const user = userEvent.setup();

    renderApp('/songs/n8-7');
    const region = await section();
    await within(region).findAllByTestId('song-audio-file');

    await user.click(within(region).getByRole('button', { name: 'Remove association: Take.mp3' }));

    await waitFor(() => {
      expect(server.associationWrites).toHaveLength(1);
    });
    expect(screen.queryByTestId('preference-warning')).toBeNull();
  });
});

describe('the preferred file rules', () => {
  it('say what plays now and why the preferred file is not the one', () => {
    const first = testGeneration('1', 1);
    const wav = testSongAudioFile('Take.wav', first, { playsForGeneration: true });
    const away = testSongAudioFile('Take.mp3', first, { isPreferred: true, status: 'unavailable' });
    const lone = testSongAudioFile('Lone.mp3', first, { isPreferred: true, status: 'missing' });
    const master = testSongAudioFile('Master.wav', null, { isPreferred: true, status: 'missing' });

    expect(playsNow(wav)).toBe(true);
    expect(playsNow(away)).toBe(false);
    expect(playsNow(testSongAudioFile('Song.wav', null, { playsForSong: true }))).toBe(true);
    expect(preferenceNote(wav, [wav, away])).toBeUndefined();
    expect(preferenceNote(away, [wav, away])).toBe(
      'Preferred, but Unavailable: Take.wav plays instead.',
    );
    expect(preferenceNote(lone, [lone])).toBe(
      'Preferred, but Missing: no other file of this Generation is available, so none plays.',
    );
    expect(preferenceNote(master, [master])).toBe(
      'Preferred for the Song, but Missing: no local file plays for the Song.',
    );
  });
});
