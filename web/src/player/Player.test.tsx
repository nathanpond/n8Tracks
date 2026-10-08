import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { UnmatchedFile } from '../api/audioFiles';
import type { Generation } from '../api/generations';
import { installFakeAudio, type FakeAudio } from '../test/fakeAudio';
import {
  isSessionRequest,
  jsonResponse,
  renderApp,
  requestPath,
  signedInSession,
} from '../test/helpers';
import {
  testGeneration,
  testSongAudioFile,
  testVersion,
  versionServer,
} from '../test/versionServer';
import { PLAYER_CHANNEL, VOLUME_STORAGE_KEY } from './playerRules';

/**
 * The player (#218) in the signed-in app, over a fake audio element: Play on a Generation row asks the
 * playback rule and plays its file; the bar shows what is playing, plays, pauses, seeks, sets the
 * volume and mute (remembered), stops at the end, says when the audio fails (or brings up sign-in
 * when the session ended), and closes; the row mirrors the bar; playing something else replaces it;
 * one audio element serves every page; and the space bar is never taken from a text field.
 */

const ONE = testVersion('1', { current: true, isFrozen: true });
const PLAYABLE = { playable: true, reason: null };
const G1 = 'n8-7-v1-g1';
const G2 = 'n8-7-v1-g2';

let fake: FakeAudio;

beforeEach(() => {
  fake = installFakeAudio();
});

afterEach(() => {
  fake.restore();
});

/** What the playback read answers for a Generation whose rule names `file`. */
function localPlayback(file: UnmatchedFile) {
  return {
    source: 'local',
    audioFile: {
      id: file.id,
      fileName: file.fileName,
      format: file.format,
      durationSeconds: file.durationSeconds,
      contentUrl: `/api/v1/audio-files/${file.id}/content`,
    },
    reason: 'format_order',
  };
}

/** A Song with two playable Generations (a WAV each) and a Song-level file. */
function catalog() {
  const { server } = versionServer([ONE]);
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
  server.playback.set(first.id, localPlayback(firstFile));
  server.playback.set(second.id, localPlayback(secondFile));
  return { server, first, second, firstFile, secondFile, master };
}

async function openSong(path = '/songs/n8-7') {
  const rendered = renderApp(path);
  const section = await screen.findByRole('region', { name: 'Versions and Generations' });
  await within(section).findByRole('table', { name: 'Versions and Generations' });
  const expander = within(section).getByRole('button', { name: 'Generations of Version 1' });
  if (expander.getAttribute('aria-expanded') !== 'true') {
    await userEvent.click(expander);
  }
  await within(section).findByRole('table', { name: 'Generations of Version 1' });
  return rendered;
}

function generationRow(shortcode: string): HTMLElement {
  const row = document.querySelector(`tr[data-generation="${shortcode}"]`);
  if (!(row instanceof HTMLElement)) {
    throw new Error(`Generation ${shortcode} is not listed.`);
  }
  return row;
}

function fileRow(path: string): HTMLElement {
  const row = within(screen.getByRole('region', { name: 'Audio Files' }))
    .getAllByTestId('song-audio-file')
    .find((candidate) => candidate.getAttribute('data-file') === path);
  if (row === undefined) {
    throw new Error(`${path} is not listed.`);
  }
  return row;
}

async function bar(): Promise<HTMLElement> {
  return screen.findByRole('contentinfo', { name: 'Player' });
}

function toggle(): HTMLElement {
  return screen.getByTestId('player-toggle');
}

async function play(generation: Generation) {
  await userEvent.click(
    within(generationRow(generation.shortcode)).getByRole('button', {
      name: `Play ${generation.shortcode}`,
    }),
  );
  await waitFor(() => {
    expect(fake.element().paused).toBe(false);
  });
}

describe('the player', () => {
  it('plays the file the playback rule names from a Generation row, shows what plays, and mirrors the row', async () => {
    const { server, first, firstFile } = catalog();
    await openSong();
    expect(screen.queryByRole('contentinfo', { name: 'Player' })).toBeNull();

    await play(first);

    const shown = await bar();
    expect(server.playbackReads).toEqual([first.id]);
    expect(fake.element().getAttribute('src')).toBe(
      new URL(`/api/v1/audio-files/${firstFile.id}/content`, document.baseURI).toString(),
    );
    expect(within(shown).getByRole('link', { name: 'Running in a Pack' })).toHaveAttribute(
      'href',
      '/songs/n8-7',
    );
    expect(within(shown).getByTestId('player-detail')).toHaveTextContent(`Version 1 · ${G1} · WAV`);
    expect(toggle()).toHaveTextContent('Pause');

    // The row shows Pause, and so does the file's row; pausing there pauses the bar.
    const rowControl = within(generationRow(G1)).getByRole('button', { name: `Pause ${G1}` });
    expect(
      within(fileRow('Take one.wav')).getByRole('button', { name: 'Pause Take one.wav' }),
    ).toBeVisible();
    await userEvent.click(rowControl);
    expect(fake.element().paused).toBe(true);
    expect(toggle()).toHaveTextContent('Play');
    expect(within(generationRow(G1)).getByRole('button', { name: `Play ${G1}` })).toBeVisible();

    // Play on the paused current item resumes it: no second lookup, and not from the start.
    fake.advance(12);
    await userEvent.click(within(generationRow(G1)).getByRole('button', { name: `Play ${G1}` }));
    expect(fake.element().paused).toBe(false);
    expect(fake.time()).toBe(12);
    expect(server.playbackReads).toHaveLength(1);
    expect(toggle()).toHaveTextContent('Pause');
  });

  it('plays a file row, labels a Song-level file, and replaces what was playing', async () => {
    const { first, master } = catalog();
    await openSong();
    await play(first);

    await userEvent.click(
      within(fileRow('Master.wav')).getByRole('button', { name: 'Play Master.wav' }),
    );
    const shown = await bar();
    await waitFor(() => {
      expect(fake.element().getAttribute('src')).toContain(
        `/api/v1/audio-files/${master.id}/content`,
      );
    });
    expect(fake.element().paused).toBe(false);
    expect(within(shown).getByTestId('player-detail')).toHaveTextContent(
      'Song-level file · Master.wav',
    );
    expect(within(generationRow(G1)).getByRole('button', { name: `Play ${G1}` })).toBeVisible();
    expect(
      within(fileRow('Master.wav')).getByRole('button', { name: 'Pause Master.wav' }),
    ).toBeVisible();
    // One element only: replacing never makes another.
    expect(document.querySelectorAll('audio')).toHaveLength(1);
  });

  it('plays another Generation in place of the first, from its beginning', async () => {
    const { first, second, secondFile } = catalog();
    await openSong();
    await play(first);
    fake.loaded(125);
    fake.advance(30);

    await play(second);

    expect(fake.element().getAttribute('src')).toContain(secondFile.id);
    expect(fake.time()).toBe(0);
    expect(within(generationRow(G1)).getByRole('button', { name: `Play ${G1}` })).toBeVisible();
    expect(within(generationRow(G2)).getByRole('button', { name: `Pause ${G2}` })).toBeVisible();
    expect(within(await bar()).getByTestId('player-detail')).toHaveTextContent(G2);
  });

  it('seeks, sets the volume, and mutes by mouse and keyboard, and remembers the volume', async () => {
    const { first } = catalog();
    const { unmount } = await openSong();
    await play(first);
    fake.loaded(125);
    fake.advance(10);

    const seekBar = screen.getByRole('slider', { name: 'Seek' });
    expect(screen.getByTestId('player-elapsed')).toHaveTextContent('0:10');
    expect(screen.getByTestId('player-duration')).toHaveTextContent('2:05');
    expect(seekBar).toHaveAttribute('aria-valuetext', '0:10 of 2:05');

    seekBar.focus();
    await userEvent.keyboard('{ArrowRight}');
    expect(fake.time()).toBe(15);
    await userEvent.keyboard('{PageUp}');
    expect(fake.time()).toBe(45);
    await userEvent.keyboard('{ArrowLeft}');
    expect(fake.time()).toBe(40);
    await userEvent.keyboard('{PageDown}');
    expect(fake.time()).toBe(10);
    await userEvent.keyboard('{End}');
    expect(fake.time()).toBe(125);
    await userEvent.keyboard('{Home}');
    expect(fake.time()).toBe(0);
    fireEvent.change(seekBar, { target: { value: '60' } });
    expect(fake.time()).toBe(60);
    expect(seekBar).toHaveAttribute('aria-valuetext', '1:00 of 2:05');

    // The space bar on the seek bar plays and pauses.
    await userEvent.keyboard(' ');
    expect(fake.element().paused).toBe(true);
    await userEvent.keyboard(' ');
    expect(fake.element().paused).toBe(false);

    const volume = screen.getByRole('slider', { name: 'Volume' });
    fireEvent.change(volume, { target: { value: '40' } });
    expect(fake.element().volume).toBeCloseTo(0.4);
    expect(volume).toHaveAttribute('aria-valuetext', '40%');

    // Space on any other button activates that button, and leaves playback alone.
    const mute = screen.getByRole('button', { name: 'Mute' });
    mute.focus();
    await userEvent.keyboard(' ');
    expect(fake.element().muted).toBe(true);
    expect(mute).toHaveAttribute('aria-pressed', 'true');
    expect(volume).toHaveAttribute('aria-valuetext', '40%, muted');
    expect(fake.element().paused).toBe(false);
    expect(JSON.parse(window.localStorage.getItem(VOLUME_STORAGE_KEY) ?? 'null')).toEqual({
      volume: 0.4,
      muted: true,
    });

    // After a reload, what was playing is gone, but the volume and mute are as they were left.
    unmount();
    await openSong();
    expect(screen.queryByRole('contentinfo', { name: 'Player' })).toBeNull();
    await play(first);
    expect(fake.element().volume).toBeCloseTo(0.4);
    expect(fake.element().muted).toBe(true);
    expect(screen.getByRole('slider', { name: 'Volume' })).toHaveValue('40');
  });

  it('stops at the end, back at the start, starts nothing else, and Play replays it', async () => {
    const { first } = catalog();
    await openSong();
    await play(first);
    fake.loaded(125);
    const plays = fake.plays();

    fake.end();

    expect(fake.element().paused).toBe(true);
    expect(fake.time()).toBe(0);
    expect(screen.getByTestId('player-elapsed')).toHaveTextContent('0:00');
    expect(toggle()).toHaveTextContent('Play');
    expect(fake.plays()).toBe(plays);
    expect(within(generationRow(G2)).getByRole('button', { name: `Play ${G2}` })).toBeVisible();

    await userEvent.click(toggle());
    expect(fake.element().paused).toBe(false);
    expect(fake.plays()).toBe(plays + 1);
  });

  it('says in words when the audio cannot be played, names the file, and offers Retry and Close', async () => {
    const { first, firstFile } = catalog();
    await openSong();
    await play(first);
    fake.loaded(125);
    fake.advance(20);

    fake.fail();

    const alert = await within(await bar()).findByRole('alert');
    expect(alert).toHaveTextContent(
      'Take one.wav could not be played: the audio could not be loaded or decoded.',
    );
    expect(toggle()).toHaveTextContent('Play');
    // Nothing else is affected: the page is as it was.
    expect(screen.getByRole('region', { name: 'Versions and Generations' })).toBeVisible();

    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(fake.element().getAttribute('src')).toContain(firstFile.id);
    expect(fake.element().paused).toBe(false);
    fake.loaded(125);
    expect(fake.time()).toBe(20);
    expect(screen.queryByTestId('player-error')).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Close the player' }));
    expect(screen.queryByRole('contentinfo', { name: 'Player' })).toBeNull();
    expect(fake.element().paused).toBe(true);
    expect(fake.element().hasAttribute('src')).toBe(false);
  });

  it('brings up sign-in instead of the error when the session has ended, and plays on from there after', async () => {
    const { first, firstFile } = catalog();
    await openSong();
    await play(first);
    fake.loaded(125);
    fake.advance(33);

    // The session ends: every request now meets the 401, until the user signs in again.
    const answer = globalThis.fetch;
    let ended = true;
    vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
      if (requestPath(input).endsWith('/api/v1/session') && init?.method === 'POST') {
        ended = false;
        return Promise.resolve(jsonResponse(201, signedInSession));
      }
      return ended && isSessionRequest(input, init)
        ? Promise.resolve(jsonResponse(401, { code: 'not_authenticated' }))
        : answer(input, init);
    });
    fake.fail(2);

    const prompt = await screen.findByRole('dialog', { name: 'Your session has ended' });
    expect(screen.queryByTestId('player-error')).toBeNull();
    // The bar stays beneath it, as it was: paused where it stopped.
    expect(screen.getByTestId('player-bar')).toHaveAttribute('data-status', 'paused');
    expect(screen.getByTestId('player-elapsed')).toHaveTextContent('0:33');

    const form = within(prompt).getByRole('form', { name: 'Sign in' });
    await userEvent.type(within(form).getByLabelText(/^Password/), 'a long passphrase');
    await userEvent.click(within(form).getByRole('button', { name: 'Sign in' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Your session has ended' })).toBeNull();
    });

    // Play carries on from where it stopped.
    await userEvent.click(toggle());
    expect(fake.element().getAttribute('src')).toContain(firstFile.id);
    fake.loaded(125);
    expect(fake.time()).toBe(33);
    expect(fake.element().paused).toBe(false);
  });

  it('says when playback could not start, and leaves what was playing alone', async () => {
    const { server, first, master } = catalog();
    await openSong();
    await userEvent.click(
      within(fileRow('Master.wav')).getByRole('button', { name: 'Play Master.wav' }),
    );
    await waitFor(() => {
      expect(fake.element().paused).toBe(false);
    });

    server.nextPlayback = () => jsonResponse(500, { code: 'internal_error' });
    await userEvent.click(within(generationRow(G1)).getByRole('button', { name: `Play ${G1}` }));

    expect(await screen.findByTestId('player-notice')).toHaveTextContent(
      'Playback could not start: n8Tracks did not answer as expected. Try again.',
    );
    expect(fake.element().getAttribute('src')).toContain(master.id);
    expect(fake.element().paused).toBe(false);

    // The rule naming nothing (the files went Missing since the list was read) says so too.
    server.playback.delete(first.id);
    await userEvent.click(within(generationRow(G1)).getByRole('button', { name: `Play ${G1}` }));
    await waitFor(() => {
      expect(screen.getByTestId('player-notice')).toHaveTextContent(
        `Playback could not start: ${G1} has no local audio file that can play, and there is no Suno audio to stream.`,
      );
    });
    expect(fake.element().getAttribute('src')).toContain(master.id);
  });

  it('disables Play, focusably and with its reason, where nothing can play', async () => {
    const { server } = versionServer([ONE]);
    const none = testGeneration('1', 1);
    const missing = testGeneration('1', 2, {
      audioFiles: { count: 1, missing: 1, unavailable: 0, formats: ['wav'] },
    });
    server.generations = [none, missing];
    server.audioFiles = [
      testSongAudioFile('Gone.wav', missing, { status: 'missing' }),
      testSongAudioFile('Away.wav', null, { status: 'unavailable' }),
    ];
    await openSong();

    const disabled = within(generationRow(G1)).getByRole('button', { name: `Play ${G1}` });
    expect(disabled).toHaveAttribute('aria-disabled', 'true');
    expect(disabled).toHaveAccessibleDescription(
      'Nothing to play: this Generation has no local audio file, and there is no Suno audio to stream.',
    );
    disabled.focus();
    expect(disabled).toHaveFocus();
    await userEvent.click(disabled);
    expect(server.playbackReads).toEqual([]);
    expect(screen.queryByRole('contentinfo', { name: 'Player' })).toBeNull();

    expect(
      within(generationRow(G2)).getByRole('button', { name: `Play ${G2}` }),
    ).toHaveAccessibleDescription(
      'Nothing to play: its audio file is Missing, and there is no Suno audio to stream.',
    );
    const gone = within(fileRow('Gone.wav')).getByRole('button', { name: 'Play Gone.wav' });
    expect(gone).toHaveAttribute('aria-disabled', 'true');
    expect(gone).toHaveAccessibleDescription(
      'Cannot play: the file is Missing from the media folder.',
    );
    expect(
      within(fileRow('Away.wav')).getByRole('button', { name: 'Play Away.wav' }),
    ).toHaveAccessibleDescription('Cannot play: the media folder cannot be read.');
    await userEvent.click(gone);
    expect(fake.touched.filter((element) => element.hasAttribute('src'))).toEqual([]);
  });

  it('keeps one audio element, playing, across pages, Version switches, and dialogs', async () => {
    const { first } = catalog();
    const { router } = await openSong();
    await play(first);
    const element = fake.element();

    await act(() => router.navigate('/settings/account'));
    expect(fake.element()).toBe(element);
    expect(element.paused).toBe(false);
    expect(await bar()).toBeVisible();

    await act(() => router.navigate('/library/unmatched'));
    await act(() => router.navigate('/songs/n8-7/v/1'));
    await act(() => router.navigate(`/songs/n8-7/generations/${G1}`));
    const panel = await screen.findByRole('dialog', { name: `Generation ${G1}` });
    // The panel's control mirrors the bar too (once the drawer's opening transition is over).
    await waitFor(() => {
      expect(within(panel).getByRole('button', { name: `Pause ${G1}` })).toBeVisible();
    });
    expect(fake.element()).toBe(element);
    expect(element.paused).toBe(false);
    expect(document.querySelectorAll('audio')).toHaveLength(1);
    expect(fake.touched).toEqual([element]);
  });

  it('never takes the space bar from a text field', async () => {
    const { first } = catalog();
    await openSong();
    await play(first);

    const goTo = screen.getByRole('textbox', { name: 'Go to' });
    await userEvent.type(goTo, 'a b');

    expect(goTo).toHaveValue('a b');
    expect(fake.element().paused).toBe(false);
  });

  it('pauses when another tab of the app starts playing', async () => {
    const { first } = catalog();
    await openSong();
    await play(first);

    const other = new BroadcastChannel(PLAYER_CHANNEL);
    other.postMessage({ type: 'playing', tab: 'another tab' });
    await waitFor(() => {
      expect(fake.element().paused).toBe(true);
    });
    other.close();
    expect(toggle()).toHaveTextContent('Play');
  });

  it('stops and closes the bar when the user signs out', async () => {
    const { first } = catalog();
    const { router } = await openSong();
    await play(first);
    const element = fake.element();

    await userEvent.click(screen.getByRole('button', { name: /owner/ }));
    const signOut = await screen.findByRole('menuitem', { name: 'Sign out' });
    vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) =>
      Promise.resolve(
        (init?.method ?? 'GET').toUpperCase() === 'DELETE'
          ? new Response(null, { status: 204 })
          : isSessionRequest(input, init)
            ? jsonResponse(401, { code: 'not_authenticated' })
            : jsonResponse(404, { code: 'not_found' }),
      ),
    );
    await userEvent.click(signOut);

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/sign-in');
    });
    expect(element.paused).toBe(true);
    expect(element.hasAttribute('src')).toBe(false);
    expect(screen.queryByRole('contentinfo', { name: 'Player' })).toBeNull();
  });
});
