import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { UnmatchedFile } from '../api/audioFiles';
import type { Generation } from '../api/generations';
import type { Song } from '../api/songs';
import { installFakeAudio, type FakeAudio } from '../test/fakeAudio';
import { jsonResponse, renderApp } from '../test/helpers';
import { entryOf, HIGHWAY, playlistServer, SUNRISE, testPlaylist } from '../test/playlistServer';
import { baseSong } from '../test/songServer';
import {
  testGeneration,
  testSongAudioFile,
  testVersion,
  versionServer,
} from '../test/versionServer';

/**
 * Play on a Song (#219): the Song page header plays the Song's choice (its Selected Generation's
 * file, or its Song-level preferred file) and the bar says which rule chose it; with nothing selected
 * the chooser lists the candidates and plays the pick for this one listen, selecting it only when the
 * separate option is ticked (a conflict is reported and playback still starts); the Selected
 * Generation with nothing to play says so with Open in Suno; a Song with nothing to offer shows Play
 * disabled with the reason; and a Song played from a Playlist stops at its end, nothing following it.
 */

const ONE = testVersion('1', { current: true, isFrozen: true });
const PLAYABLE = { playable: true, reason: null };
const G1 = 'n8-7-v1-g1';
const G2 = 'n8-7-v1-g2';
const SUNO_ID = '0c90d621-e30c-4c76-814a-e1fdeb500582';

let fake: FakeAudio;

beforeEach(() => {
  fake = installFakeAudio();
});

afterEach(() => {
  fake.restore();
});

function playbackFile(file: UnmatchedFile) {
  return {
    id: file.id,
    fileName: file.fileName,
    format: file.format,
    durationSeconds: file.durationSeconds,
    contentUrl: `/api/v1/audio-files/${file.id}/content`,
  };
}

function generationCandidate(generation: Generation, change: Record<string, unknown> = {}) {
  return {
    kind: 'generation',
    generation: { id: generation.id, shortcode: generation.shortcode },
    versionNumber: '1',
    rating: null,
    durationSeconds: 93,
    state: 'active',
    remoteState: 'present',
    playable: true,
    reason: null,
    ...change,
  };
}

/** The Song with two Generations (a WAV each) and a Song-level file, Selected as `song` says. */
function catalog(song: Partial<Song> = {}) {
  const { server } = versionServer([ONE], { ...baseSong, ...song });
  const first = testGeneration('1', 1, {
    playback: PLAYABLE,
    audioFiles: { count: 1, missing: 0, unavailable: 0, formats: ['wav'] },
  });
  const second = testGeneration('1', 2, {
    playback: PLAYABLE,
    audioFiles: { count: 1, missing: 0, unavailable: 0, formats: ['wav'] },
  });
  const firstFile = testSongAudioFile('Take one.wav', first, { playsForGeneration: true });
  const secondFile = testSongAudioFile('Take two.wav', second, { playsForGeneration: true });
  const master = testSongAudioFile('Master.wav', null);
  server.generations = [first, second];
  server.audioFiles = [master, firstFile, secondFile];
  server.playback.set(first.id, {
    source: 'local',
    audioFile: playbackFile(firstFile),
    reason: 'format_order',
  });
  server.playback.set(second.id, {
    source: 'local',
    audioFile: playbackFile(secondFile),
    reason: 'format_order',
  });
  return { server, first, second, firstFile, secondFile, master };
}

/** The answer when nothing is selected: the Song-level file, G1 (rated), and G2 (Archived, nothing to play). */
function needsChoice(parts: ReturnType<typeof catalog>) {
  return {
    source: 'none',
    audioFile: null,
    reason: 'no_selected_generation',
    generation: null,
    state: 'needs-choice',
    candidates: [
      { kind: 'file', audioFile: playbackFile(parts.master), playable: true, reason: null },
      generationCandidate(parts.first, { rating: 4 }),
      generationCandidate(parts.second, {
        state: 'archived',
        playable: false,
        reason: 'nothing_available',
      }),
    ],
  };
}

async function openSong() {
  renderApp('/songs/n8-7');
  await screen.findByRole('heading', { level: 2, name: baseSong.title });
  const section = await screen.findByRole('region', { name: 'Versions and Generations' });
  await within(section).findByRole('table', { name: 'Versions and Generations' });
  const expander = within(section).getByRole('button', { name: 'Generations of Version 1' });
  if (expander.getAttribute('aria-expanded') !== 'true') {
    await userEvent.click(expander);
  }
  await within(section).findByRole('table', { name: 'Generations of Version 1' });
}

function headerPlay(): HTMLElement {
  return screen.getByTestId('song-play-button');
}

function generationRowButton(shortcode: string): HTMLElement {
  const row = document.querySelector(`tr[data-generation="${shortcode}"]`);
  if (!(row instanceof HTMLElement)) {
    throw new Error(`Generation ${shortcode} is not listed.`);
  }
  return within(row).getByTestId('play-button');
}

/** The chooser's option at `index`. */
function radio(dialog: HTMLElement, index: number): HTMLElement {
  const option = within(dialog).getAllByRole('radio')[index];
  if (option === undefined) {
    throw new Error(`The chooser has no option ${String(index)}.`);
  }
  return option;
}

async function bar(): Promise<HTMLElement> {
  return screen.findByRole('contentinfo', { name: 'Player' });
}

async function playing() {
  await waitFor(() => {
    expect(fake.element().paused).toBe(false);
  });
}

describe('Play on a Song', () => {
  it('plays the Selected Generation’s file from the header, names the rule, and pauses without asking again', async () => {
    const parts = catalog({
      hasSelectedGeneration: true,
      selectedGeneration: { id: '', shortcode: G1, state: 'active', remoteState: 'present' },
      playback: { state: 'ready', reason: null },
    });
    const { server, first, firstFile } = parts;
    server.song = {
      ...server.song,
      selectedGeneration: { id: first.id, shortcode: G1, state: 'active', remoteState: 'present' },
    };
    server.songPlayback = {
      source: 'local',
      audioFile: playbackFile(firstFile),
      reason: 'format_order',
      generation: { id: first.id, shortcode: G1, sunoId: SUNO_ID },
      state: 'ready',
      candidates: [],
    };
    await openSong();

    await userEvent.click(headerPlay());
    await playing();

    const shown = await bar();
    expect(fake.element().getAttribute('src')).toBe(
      new URL(`/api/v1/audio-files/${firstFile.id}/content`, document.baseURI).toString(),
    );
    expect(within(shown).getByTestId('player-detail')).toHaveTextContent(`Version 1 · ${G1} · WAV`);
    expect(within(shown).getByTestId('player-via')).toHaveTextContent(
      'The Song’s choice: its Selected Generation',
    );
    expect(headerPlay()).toHaveAccessibleName(`Pause ${baseSong.title}`);
    // The Selected Generation's own row mirrors it.
    await waitFor(() => {
      expect(generationRowButton(G1)).toHaveAttribute('data-playing', 'true');
    });

    // Pressing it again pauses, and then resumes: it never asks again.
    await userEvent.click(headerPlay());
    expect(fake.element().paused).toBe(true);
    await userEvent.click(headerPlay());
    expect(fake.element().paused).toBe(false);
    expect(server.songPlaybackReads).toBe(1);
    expect(server.playbackReads).toEqual([]);
  });

  it('plays the Song-level preferred file and says the Song chose it', async () => {
    const parts = catalog({ playback: { state: 'ready', reason: null } });
    parts.server.songPlayback = {
      source: 'local',
      audioFile: playbackFile(parts.master),
      reason: 'song_preferred',
      generation: null,
      state: 'ready',
      candidates: [],
    };
    await openSong();

    await userEvent.click(headerPlay());
    await playing();

    const shown = await bar();
    expect(fake.element().getAttribute('src')).toContain(parts.master.id);
    expect(within(shown).getByTestId('player-detail')).toHaveTextContent(
      'Song-level file · Master.wav',
    );
    expect(within(shown).getByTestId('player-via')).toHaveTextContent(
      'The Song’s choice: its Song-level file',
    );
  });

  it('asks which Generation when none is selected, plays the pick for this listen without selecting it, and asks again next time', async () => {
    const parts = catalog({
      playback: { state: 'needs-choice', reason: 'no_selected_generation' },
    });
    const { server, first, secondFile } = parts;
    server.songPlayback = needsChoice(parts);
    await openSong();
    const control = headerPlay();

    await userEvent.click(control);

    const dialog = await screen.findByRole('dialog', { name: `Play ${baseSong.title}` });
    expect(fake.element().paused).toBe(true);
    const options = within(dialog).getAllByRole('radio');
    expect(
      options.map((option) =>
        option instanceof HTMLInputElement ? option.labels?.[0]?.textContent : undefined,
      ),
    ).toEqual([
      expect.stringContaining('Song-level file Master.wav'),
      expect.stringContaining(`Version 1 · ${G1}`),
      expect.stringContaining(`Version 1 · ${G2}`),
    ]);
    expect(within(dialog).getByText(/4 of 5 stars · 1:33/)).toBeInTheDocument();
    expect(within(dialog).getByText('Archived')).toBeInTheDocument();
    expect(options[2]).toBeDisabled();
    expect(
      within(dialog).getByText(/Nothing to play: it has no local audio file/),
    ).toBeInTheDocument();
    // Nothing picked yet: Play waits, and the selection option is not offered.
    expect(within(dialog).getByTestId('play-chosen')).toBeDisabled();
    expect(within(dialog).queryByRole('checkbox')).toBeNull();

    // A Song-level file offers no selection option; a Generation does, unticked.
    await userEvent.click(radio(dialog, 0));
    expect(within(dialog).queryByRole('checkbox')).toBeNull();
    await userEvent.click(radio(dialog, 1));
    const option = within(dialog).getByRole('checkbox', {
      name: 'Make this the Selected Generation',
    });
    expect(option).not.toBeChecked();

    await userEvent.click(within(dialog).getByTestId('play-chosen'));
    await playing();

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    // Focus goes back to the Play control.
    await waitFor(() => {
      expect(control).toHaveFocus();
    });
    expect(server.playbackReads).toEqual([first.id]);
    expect(server.selectionWrites).toEqual([]);
    const shown = await bar();
    expect(within(shown).getByTestId('player-via')).toHaveTextContent('Chosen for this listen');
    expect(within(shown).getByTestId('player-detail')).toHaveTextContent(G1);
    expect(fake.element().getAttribute('src')).not.toContain(secondFile.id);

    // While it plays, the Song's Play pauses it; it does not ask again.
    await userEvent.click(headerPlay());
    expect(fake.element().paused).toBe(true);
    expect(server.songPlaybackReads).toBe(1);

    // Once the bar is closed, the next Play asks again.
    await userEvent.click(within(shown).getByRole('button', { name: 'Close the player' }));
    await userEvent.click(headerPlay());
    await screen.findByRole('dialog', { name: `Play ${baseSong.title}` });
    expect(server.songPlaybackReads).toBe(2);
  });

  it('makes the pick the Selected Generation only when ticked, with the Song’s revision', async () => {
    const parts = catalog({
      playback: { state: 'needs-choice', reason: 'no_selected_generation' },
    });
    const { server, first } = parts;
    server.songPlayback = needsChoice(parts);
    await openSong();

    await userEvent.click(headerPlay());
    const dialog = await screen.findByRole('dialog', { name: `Play ${baseSong.title}` });
    await userEvent.click(radio(dialog, 1));
    await userEvent.click(
      within(dialog).getByRole('checkbox', { name: 'Make this the Selected Generation' }),
    );
    await userEvent.click(within(dialog).getByTestId('play-chosen'));
    await playing();

    expect(server.selectionWrites).toEqual([
      { method: 'PUT', ifMatch: `"${String(baseSong.revision)}"`, body: { generation: first.id } },
    ]);
    expect(within(await bar()).getByTestId('player-via')).toHaveTextContent(
      'The Song’s choice: its Selected Generation',
    );
  });

  it('reports a conflict on selecting, and still plays the pick', async () => {
    const parts = catalog({
      playback: { state: 'needs-choice', reason: 'no_selected_generation' },
    });
    const { server, first } = parts;
    server.songPlayback = needsChoice(parts);
    server.nextSelectionWrite = () =>
      jsonResponse(409, {
        code: 'revision_conflict',
        current: { ...server.song, revision: server.song.revision + 1 },
      });
    await openSong();

    await userEvent.click(headerPlay());
    const dialog = await screen.findByRole('dialog', { name: `Play ${baseSong.title}` });
    await userEvent.click(radio(dialog, 1));
    await userEvent.click(
      within(dialog).getByRole('checkbox', { name: 'Make this the Selected Generation' }),
    );
    await userEvent.click(within(dialog).getByTestId('play-chosen'));
    await playing();

    const shown = await bar();
    expect(server.playbackReads).toEqual([first.id]);
    expect(within(shown).getByTestId('player-notice')).toHaveTextContent(
      `${G1} was not made the Selected Generation`,
    );
    expect(within(shown).getByTestId('player-via')).toHaveTextContent('Chosen for this listen');
  });

  it('says the Selected Generation has nothing to play, with Open in Suno, and offers no chooser', async () => {
    const parts = catalog({
      playback: { state: 'selected-unplayable', reason: 'nothing_available' },
    });
    parts.server.songPlayback = {
      source: 'none',
      audioFile: null,
      reason: 'nothing_available',
      generation: { id: parts.first.id, shortcode: G1, sunoId: SUNO_ID },
      state: 'selected-unplayable',
      candidates: [],
    };
    await openSong();

    await userEvent.click(headerPlay());

    const shown = await bar();
    await waitFor(() => {
      expect(within(shown).getByTestId('player-notice')).toHaveTextContent(
        `Nothing to play: ${baseSong.title}’s Selected Generation, ${G1}, has no local audio file`,
      );
    });
    expect(
      within(shown).getByRole('link', { name: 'Open in Suno (opens a new tab)' }),
    ).toHaveAttribute('href', `https://suno.com/song/${SUNO_ID}`);
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(fake.element().paused).toBe(true);
  });

  it('shows Play disabled with the reason when the Song has nothing to offer', async () => {
    const { server } = catalog({ playback: { state: 'none', reason: 'no_generations' } });
    await openSong();

    const control = headerPlay();
    expect(control).toHaveAttribute('aria-disabled', 'true');
    expect(control).toHaveAccessibleDescription(
      'Nothing to play: this Song has no Generations and no audio files.',
    );
    await userEvent.click(control);
    expect(server.songPlaybackReads).toBe(0);
    expect(screen.queryByRole('contentinfo', { name: 'Player' })).toBeNull();
  });
});

describe('Play on a Playlist’s Song', () => {
  it('plays that Song only: at its end nothing else starts', async () => {
    const fileId = '0199c300-0000-7000-8000-000000000001';
    const playlist = testPlaylist('Road trip', [HIGHWAY, SUNRISE], {
      songs: [
        {
          ...entryOf(HIGHWAY),
          hasSelectedGeneration: true,
          playback: { state: 'ready', reason: null },
        },
        { ...entryOf(SUNRISE), playback: { state: 'none', reason: 'no_generations' } },
      ],
    });
    const server = playlistServer([playlist]);
    server.songPlayback.set(HIGHWAY.id, {
      source: 'local',
      audioFile: {
        id: fileId,
        fileName: 'Highway.wav',
        format: 'wav',
        durationSeconds: 90,
        contentUrl: `/api/v1/audio-files/${fileId}/content`,
      },
      reason: 'format_order',
      generation: {
        id: '0199c300-0000-7000-8000-0000000000a1',
        shortcode: 'n8-1-v1-g1',
        sunoId: null,
      },
      state: 'ready',
      candidates: [],
    });
    renderApp(`/playlists/${playlist.id}`);

    const rows = await screen.findAllByRole('listitem');
    const highway = rows.find((row) => row.getAttribute('data-song-id') === HIGHWAY.id);
    const sunrise = rows.find((row) => row.getAttribute('data-song-id') === SUNRISE.id);
    if (highway === undefined || sunrise === undefined) {
      throw new Error('The Playlist’s Songs are not listed.');
    }
    expect(within(sunrise).getByTestId('song-play-button')).toHaveAttribute(
      'aria-disabled',
      'true',
    );

    await userEvent.click(within(highway).getByRole('button', { name: `Play ${HIGHWAY.title}` }));
    await playing();
    fake.loaded(90);
    fake.end();

    await waitFor(() => {
      expect(screen.getByTestId('player-bar')).toHaveAttribute('data-status', 'paused');
    });
    expect(fake.element().getAttribute('src')).toContain(fileId);
    expect(fake.plays()).toBe(1);
    expect(server.songPlaybackReads).toEqual([HIGHWAY.id]);
  });
});
