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
import { fileDurationText, sizeMbText, songFolderText, tallyText } from './songAudioFilesRules';

/**
 * A Song's local audio files (#211): the Song page's Audio Files section, the Generation panel's
 * list, each Generation row's count in the Versions table, and the Change and Remove association
 * actions (#210) on each listed file.
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

describe('the Song page’s Audio Files section', () => {
  it('lists every file with its details, Song-level first, each Generation by its shortcode', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1);
    const second = testGeneration('1', 2, { state: 'archived' });
    server.generations = [first, second];
    server.audioFiles = [
      testSongAudioFile('Song.mp3', null),
      testSongAudioFile('Album/Take (suno-1).wav', first, {
        durationSeconds: 3725,
        sizeBytes: 52_428_800,
      }),
      testSongAudioFile('Album/Take two.m4a', second, { durationSeconds: null }),
    ];

    renderApp('/songs/n8-7');
    const region = await section();
    const rows = await within(region).findAllByTestId('song-audio-file');
    expect(rows.map((row) => row.getAttribute('data-file'))).toEqual([
      'Song.mp3',
      'Album/Take (suno-1).wav',
      'Album/Take two.m4a',
    ]);

    const songLevel = rowOf(region, 'Song.mp3');
    expect(within(songLevel).getByRole('rowheader')).toHaveTextContent('Song.mp3');
    expect(songLevel).toHaveTextContent('/MP32:053.0 MBAvailableSong-levelBy you');
    expect(within(songLevel).getByTestId('audio-file-origin')).toHaveTextContent('By you');

    const matched = rowOf(region, 'Album/Take (suno-1).wav');
    expect(matched).toHaveTextContent('Album');
    expect(matched).toHaveTextContent('WAV');
    expect(matched).toHaveTextContent('1:02:05');
    expect(matched).toHaveTextContent('50.0 MB');
    expect(within(matched).getByTestId('audio-file-origin')).toHaveTextContent('By Suno ID');
    expect(within(matched).getByRole('link', { name: G1 })).toHaveAttribute(
      'href',
      `/songs/n8-7/generations/${G1}`,
    );
    expect(within(matched).queryByText('Archived')).toBeNull();

    // An archived Generation's file is listed and marked; an unknown duration is a dash.
    const archived = rowOf(region, 'Album/Take two.m4a');
    expect(within(archived).getByTestId('audio-file-generation')).toHaveTextContent(
      'n8-7-v1-g2Archived',
    );
    expect(archived).toHaveTextContent('—');

    for (const row of rows) {
      expect(within(row).getByRole('button', { name: /^Change association: / })).toBeEnabled();
      expect(within(row).getByRole('button', { name: /^Remove association: / })).toBeEnabled();
    }
  });

  it('marks Missing and Unavailable files and never hides them', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1);
    server.generations = [first];
    server.audioFiles = [
      testSongAudioFile('gone.wav', first, { status: 'missing' }),
      testSongAudioFile('away.mp3', first, { status: 'unavailable' }),
      testSongAudioFile('here.mp3', first),
    ];

    renderApp('/songs/n8-7');
    const region = await section();
    await within(region).findAllByTestId('song-audio-file');

    const status = (path: string) => within(rowOf(region, path)).getByTestId('file-status');
    expect(status('gone.wav')).toHaveTextContent('Missing');
    expect(status('gone.wav')).toHaveAttribute('data-status', 'missing');
    expect(status('away.mp3')).toHaveTextContent('Unavailable');
    expect(status('here.mp3')).toHaveTextContent('Available');
    // Missing and Unavailable are badges; Available is plain text.
    expect(status('gone.wav').className).toMatch(/Badge/);
    expect(status('here.mp3').className).not.toMatch(/Badge/);
  });

  it('says a Song with no files has none and links to Unmatched Files', async () => {
    versionServer([ONE]);

    renderApp('/songs/n8-7');
    const region = await section();

    const empty = await within(region).findByTestId('no-audio-files');
    expect(empty).toHaveTextContent('No local audio files.');
    expect(within(empty).getByRole('link', { name: 'Open Unmatched Files' })).toHaveAttribute(
      'href',
      '/library/unmatched',
    );
    expect(within(region).queryByRole('table')).toBeNull();
  });

  it('removes an association at once, says so, and reads the lists again', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1);
    server.generations = [first];
    server.audioFiles = [
      testSongAudioFile('keep.wav', first),
      testSongAudioFile('drop.mp3', first, { revision: 4 }),
    ];
    const user = userEvent.setup();

    renderApp('/songs/n8-7');
    const region = await section();
    await within(region).findAllByTestId('song-audio-file');
    const generationReads = server.generationReads;

    await user.click(within(region).getByRole('button', { name: 'Remove association: drop.mp3' }));

    await waitFor(() => {
      expect(within(region).getByTestId('audio-files-announcement')).toHaveTextContent(
        'Removed the association of drop.mp3. It is back in Unmatched Files.',
      );
    });
    expect(server.associationWrites).toEqual([
      { method: 'DELETE', id: server.associationWrites[0]?.id, ifMatch: '"4"', body: null },
    ]);
    await waitFor(() => {
      expect(
        within(region)
          .getAllByTestId('song-audio-file')
          .map((row) => row.getAttribute('data-file')),
      ).toEqual(['keep.wav']);
    });
    // The Generations' counts are read again with it.
    expect(server.generationReads).toBeGreaterThan(generationReads);
  });

  it('says a stale removal changed nothing and shows the file as it is now', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1);
    server.generations = [first];
    server.audioFiles = [testSongAudioFile('drop.mp3', first, { revision: 4 })];
    const user = userEvent.setup();

    renderApp('/songs/n8-7');
    const region = await section();
    await within(region).findAllByTestId('song-audio-file');
    // Another tab changed it meanwhile.
    server.audioFiles = [testSongAudioFile('drop.mp3', null, { revision: 5 })];
    const reads = server.audioFileReads;

    await user.click(within(region).getByRole('button', { name: 'Remove association: drop.mp3' }));

    expect(await within(region).findByRole('alert')).toHaveTextContent(
      'drop.mp3 changed since the list was loaded, so its association was not removed.',
    );
    await waitFor(() => {
      expect(server.audioFileReads).toBeGreaterThan(reads);
    });
    await waitFor(() => {
      expect(
        within(rowOf(region, 'drop.mp3')).getByTestId('audio-file-generation'),
      ).toHaveTextContent('Song-level');
    });
  });

  it('opens the association dialog with the file’s current association', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1);
    server.generations = [first];
    server.audioFiles = [testSongAudioFile('Album/Take.wav', first)];
    const user = userEvent.setup();

    renderApp('/songs/n8-7');
    const region = await section();
    await within(region).findAllByTestId('song-audio-file');

    await user.click(within(region).getByRole('button', { name: 'Change association: Take.wav' }));

    const dialog = await screen.findByRole('dialog', { name: 'Change association' });
    expect(within(dialog).getByTestId('current-association')).toHaveTextContent(
      `It is associated with Running in a Pack (n8-7), Generation ${G1}, matched by its Suno ID.`,
    );
  });

  it('reads the lists again when the window regains focus', async () => {
    const { server } = versionServer([ONE]);
    server.audioFiles = [];

    renderApp('/songs/n8-7');
    const region = await section();
    await within(region).findByTestId('no-audio-files');
    const reads = server.audioFileReads;
    server.audioFiles = [testSongAudioFile('new.wav', null)];

    window.dispatchEvent(new Event('focus'));

    await waitFor(() => {
      expect(server.audioFileReads).toBeGreaterThan(reads);
    });
    expect(await within(region).findByTestId('song-audio-file')).toHaveAttribute(
      'data-file',
      'new.wav',
    );
  });
});

describe('each Generation’s files', () => {
  it('counts each Generation’s files and formats in its Versions table row, and nothing when none', async () => {
    const { server } = versionServer([ONE]);
    server.generations = [
      testGeneration('1', 1, {
        audioFiles: { count: 3, missing: 1, unavailable: 0, formats: ['wav', 'm4a', 'mp3'] },
      }),
      testGeneration('1', 2, {
        audioFiles: { count: 2, missing: 0, unavailable: 2, formats: ['flac'] },
      }),
      testGeneration('1', 3),
    ];
    const user = userEvent.setup();

    renderApp('/songs/n8-7');
    await user.click(await screen.findByRole('button', { name: 'Generations of Version 1' }));
    const table = await screen.findByRole('table', { name: 'Generations of Version 1' });

    const local = (shortcode: string) =>
      within(
        within(table)
          .getAllByRole('row')
          .find((row) => row.getAttribute('data-generation') === shortcode) ?? table,
      ).getByTestId('generation-local-files');
    expect(local('n8-7-v1-g1')).toHaveTextContent('3 files · WAV, M4A, MP3 · 1 missing');
    expect(local('n8-7-v1-g2')).toHaveTextContent('2 files · FLAC · 2 unavailable');
    expect(local('n8-7-v1-g3')).toHaveTextContent(/^$/);
  });

  it('lists the Generation’s own files in its panel, and says when it has none', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1);
    const second = testGeneration('1', 2);
    server.generations = [first, second];
    server.audioFiles = [
      testSongAudioFile('Song.mp3', null),
      testSongAudioFile('Take.wav', first),
      testSongAudioFile('Take.mp3', first, { status: 'missing' }),
    ];

    renderApp(`/songs/n8-7/generations/${G1}`);
    const panel = await screen.findByRole('dialog', { name: `Generation ${G1}` });
    const region = within(panel).getByRole('region', { name: 'Local audio files' });

    const items = await within(region).findAllByTestId('generation-audio-file');
    expect(items.map((item) => item.getAttribute('data-file'))).toEqual(['Take.wav', 'Take.mp3']);
    expect(items[0]).toHaveTextContent('/ · WAV · 2:05 · 3.0 MB · By Suno ID');
    expect(within(items[1] ?? region).getByTestId('file-status')).toHaveTextContent('Missing');
    expect(
      within(items[0] ?? region).getByRole('button', { name: 'Change association: Take.wav' }),
    ).toBeEnabled();
  });

  it('says a Generation with no files has none, without the link', async () => {
    const { server } = versionServer([ONE]);
    const first = testGeneration('1', 1);
    server.generations = [first];
    server.audioFiles = [testSongAudioFile('Song.mp3', null)];

    renderApp(`/songs/n8-7/generations/${G1}`);
    const panel = await screen.findByRole('dialog', { name: `Generation ${G1}` });
    const region = within(panel).getByRole('region', { name: 'Local audio files' });

    expect(await within(region).findByTestId('no-generation-audio-files')).toHaveTextContent(
      'No local audio files',
    );
    expect(within(region).queryByRole('link')).toBeNull();
  });
});

describe('the Song’s audio file rules', () => {
  it('words the counts, folders, durations, and sizes', () => {
    expect(tallyText({ count: 0, missing: 0, unavailable: 0, formats: [] })).toBe('');
    expect(tallyText({ count: 1, missing: 0, unavailable: 0, formats: ['wav'] })).toBe(
      '1 file · WAV',
    );
    expect(tallyText({ count: 2, missing: 1, unavailable: 0, formats: ['wav', 'mp3'] })).toBe(
      '2 files · WAV, MP3 · 1 missing',
    );
    expect(songFolderText({ path: 'a.wav', fileName: 'a.wav' })).toBe('/');
    expect(songFolderText({ path: 'One/Two/a.wav', fileName: 'a.wav' })).toBe('One/Two');
    expect(fileDurationText(null)).toBe('—');
    expect(fileDurationText(65)).toBe('1:05');
    expect(fileDurationText(3600)).toBe('1:00:00');
    expect(sizeMbText(0)).toBe('0.0 MB');
    expect(sizeMbText(1_572_864)).toBe('1.5 MB');
  });
});
