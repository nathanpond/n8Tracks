import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Generation } from '../api/generations';
import { installFakeAudio, type FakeAudio } from '../test/fakeAudio';
import { renderApp } from '../test/helpers';
import { baseSong } from '../test/songServer';
import {
  testGeneration,
  testSongAudioFile,
  testVersion,
  versionServer,
} from '../test/versionServer';
import { SUNO_STALL_TIMEOUT_MS, SUNO_START_TIMEOUT_MS } from './playerRules';

/**
 * The Suno fallback in the player (#221), over a fake audio element: a Generation with no local file
 * streams Suno's address straight from the browser (no referrer, no credentials mode), and the bar
 * says "Streaming from Suno"; a local file says "Local file" and its format. A Suno stream that errors,
 * does not start within 15 seconds, or stalls for 30 seconds shows the Suno error state: Open in Suno
 * in a new tab and the sync suggestion, with no Retry. Complement: a local file's error offers Open in
 * Suno only when its Generation has a Suno page. A Song's Selected Generation streams from Suno, and a
 * Generation with nothing to stream keeps Play disabled, with the reason and Open in Suno.
 */

const ONE = testVersion('1', { current: true, isFrozen: true });
const G1 = 'n8-7-v1-g1';
const STREAM = 'https://d2lwuy8qc234o3.cloudfront.net/1/clip/0c90d621.m4a';
const PAGE = 'https://suno.com/song/0c90d621';

let fake: FakeAudio;

beforeEach(() => {
  fake = installFakeAudio();
});

afterEach(() => {
  vi.useRealTimers();
  fake.restore();
});

function sunoPlayback() {
  return {
    source: 'suno',
    audioFile: null,
    sunoAudioUrl: STREAM,
    sunoPageUrl: PAGE,
    reason: 'suno_stream',
  };
}

/** A Song whose first Generation streams from Suno, and whose second has a local WAV (with or without a Suno page). */
function catalog(secondPage: string | null = PAGE) {
  const { server } = versionServer([ONE]);
  const streamed = testGeneration('1', 1, {
    playback: { playable: true, reason: 'suno_stream' },
  });
  const local = testGeneration('1', 2, {
    playback: { playable: true, reason: null },
    audioFiles: { count: 1, missing: 0, unavailable: 0, formats: ['wav'] },
  });
  const file = testSongAudioFile('Take two.wav', local, { playsForGeneration: true });
  server.generations = [streamed, local];
  server.audioFiles = [file];
  server.playback.set(streamed.id, sunoPlayback());
  server.playback.set(local.id, {
    source: 'local',
    audioFile: {
      id: file.id,
      fileName: file.fileName,
      format: file.format,
      durationSeconds: 125,
      contentUrl: `/api/v1/audio-files/${file.id}/content`,
    },
    sunoAudioUrl: null,
    sunoPageUrl: secondPage,
    reason: 'format_order',
  });
  return { server, streamed, local, file };
}

async function openSong() {
  renderApp('/songs/n8-7');
  const section = await screen.findByRole('region', { name: 'Versions and Generations' });
  await within(section).findByRole('table', { name: 'Versions and Generations' });
  const expander = within(section).getByRole('button', { name: 'Generations of Version 1' });
  if (expander.getAttribute('aria-expanded') !== 'true') {
    await userEvent.click(expander);
  }
  await within(section).findByRole('table', { name: 'Generations of Version 1' });
}

function generationRow(shortcode: string): HTMLElement {
  const row = document.querySelector(`tr[data-generation="${shortcode}"]`);
  if (!(row instanceof HTMLElement)) {
    throw new Error(`Generation ${shortcode} is not listed.`);
  }
  return row;
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

async function bar(): Promise<HTMLElement> {
  return screen.findByRole('contentinfo', { name: 'Player' });
}

/** The Suno error state: the alert, Open in Suno in a new tab, the sync suggestion, and no Retry. */
async function expectSunoError(shown: HTMLElement) {
  const error = await within(shown).findByTestId('player-error');
  expect(error).toHaveAttribute('data-source', 'suno');
  expect(error).toHaveTextContent(`The Suno audio of ${G1} could not be played.`);
  expect(error).toHaveTextContent('sync with Suno again');
  const link = within(shown).getByRole('link', { name: 'Open in Suno (opens a new tab)' });
  expect(link).toHaveAttribute('href', PAGE);
  expect(link).toHaveAttribute('target', '_blank');
  expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  expect(within(shown).queryByRole('button', { name: 'Retry' })).toBeNull();
  expect(within(shown).getByTestId('player-bar')).toHaveAttribute('data-status', 'error');
}

describe('the Suno fallback', () => {
  it('streams Suno’s address from the browser, with no referrer or credentials mode, and says so', async () => {
    const { streamed, local } = catalog();
    const fetches = vi.spyOn(globalThis, 'fetch');
    await openSong();

    await play(streamed);

    const shown = await bar();
    const audio = fake.element();
    // Suno's own address, as is: never through n8Tracks.
    expect(audio.getAttribute('src')).toBe(STREAM);
    expect(audio.getAttribute('referrerpolicy')).toBe('no-referrer');
    expect(audio.hasAttribute('crossorigin')).toBe(false);
    expect(audio.crossOrigin).toBeNull();
    expect(within(shown).getByTestId('player-source')).toHaveTextContent('Streaming from Suno');
    expect(within(shown).getByTestId('player-source')).toHaveAttribute('data-source', 'suno');
    expect(within(shown).getByTestId('player-detail')).toHaveTextContent(`Version 1 · ${G1}`);
    expect(within(shown).getByTestId('player-detail')).not.toHaveTextContent('·  ');
    // The app itself never asked for Suno's address.
    expect(
      fetches.mock.calls.some(([input]) =>
        (input instanceof Request ? input.url : String(input)).includes('cloudfront'),
      ),
    ).toBe(false);

    // A local file says so, with its format.
    await play(local);
    expect(within(shown).getByTestId('player-source')).toHaveTextContent('Local file · WAV');
    expect(within(shown).getByTestId('player-source')).toHaveAttribute('data-source', 'local');
    fetches.mockRestore();
  });

  it('offers Open in Suno and the sync suggestion, without Retry, when the stream fails; Play asks Suno again', async () => {
    const { streamed } = catalog();
    await openSong();
    await play(streamed);
    const shown = await bar();
    fake.started();
    const plays = fake.plays();

    fake.fail(2);

    await expectSunoError(shown);
    expect(within(shown).getByTestId('player-source')).toHaveTextContent('Streaming from Suno');
    // No retry: the request to Suno stops, and nothing plays until the user presses Play.
    expect(fake.element().hasAttribute('src')).toBe(false);
    expect(fake.plays()).toBe(plays);

    await userEvent.click(within(shown).getByTestId('player-toggle'));
    await waitFor(() => {
      expect(fake.element().getAttribute('src')).toBe(STREAM);
    });
    expect(fake.plays()).toBe(plays + 1);
  });

  it('treats a stream that has not started within 15 seconds as failed', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'], shouldAdvanceTime: true });
    const { streamed } = catalog();
    await openSong();
    await play(streamed);
    const shown = await bar();

    act(() => {
      vi.advanceTimersByTime(SUNO_START_TIMEOUT_MS - 2000);
    });
    expect(within(shown).getByTestId('player-bar')).not.toHaveAttribute('data-status', 'error');
    act(() => {
      vi.advanceTimersByTime(2000);
    });

    await expectSunoError(shown);
  });

  it('treats a stream that stalls for 30 seconds while playing as failed, and one that carries on as fine', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'], shouldAdvanceTime: true });
    const { streamed } = catalog();
    await openSong();
    await play(streamed);
    const shown = await bar();
    fake.started();

    // Started in time: the 15 seconds no longer count.
    act(() => {
      vi.advanceTimersByTime(SUNO_START_TIMEOUT_MS * 2);
    });
    expect(within(shown).getByTestId('player-bar')).toHaveAttribute('data-status', 'playing');

    // A short stall that recovers is fine.
    fake.waiting();
    act(() => {
      vi.advanceTimersByTime(SUNO_STALL_TIMEOUT_MS - 2000);
    });
    fake.started();
    act(() => {
      vi.advanceTimersByTime(SUNO_STALL_TIMEOUT_MS);
    });
    expect(within(shown).getByTestId('player-bar')).toHaveAttribute('data-status', 'playing');

    fake.waiting();
    act(() => {
      vi.advanceTimersByTime(SUNO_STALL_TIMEOUT_MS);
    });

    await expectSunoError(shown);
  });

  it('offers Open in Suno on a local file’s error only when its Generation has a Suno page, with Retry', async () => {
    const { local } = catalog();
    await openSong();
    await play(local);
    const shown = await bar();

    fake.fail();

    const error = await within(shown).findByTestId('player-error');
    expect(error).toHaveAttribute('data-source', 'local');
    expect(within(shown).getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    expect(within(shown).getByTestId('player-error-suno-link')).toHaveAttribute('href', PAGE);
  });

  it('offers no Open in Suno on a local file’s error when its Generation has no Suno page', async () => {
    const { local } = catalog(null);
    await openSong();
    await play(local);
    const shown = await bar();

    fake.fail();

    await within(shown).findByTestId('player-error');
    expect(within(shown).queryByTestId('player-error-suno-link')).toBeNull();
    expect(within(shown).getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });

  it('keeps Play disabled with the reason, beside Open in Suno, when there is nothing local and nothing to stream', async () => {
    const { server } = versionServer([ONE]);
    const silent = testGeneration('1', 1, {
      playback: { playable: false, reason: 'suno_not_present' },
      remoteState: 'trashed',
    });
    server.generations = [silent];
    await openSong();

    const row = generationRow(G1);
    const disabled = within(row).getByRole('button', { name: `Play ${G1}` });
    expect(disabled).toHaveAttribute('aria-disabled', 'true');
    expect(disabled).toHaveAccessibleDescription(
      'Nothing to play: this Generation has no local audio file, and Suno no longer lists it (in its Trash, or gone), so it is not streamed.',
    );
    expect(within(row).getByRole('link', { name: /Open in Suno/ })).toHaveAttribute(
      'href',
      `https://suno.com/song/${String(silent.sunoId)}`,
    );
    await userEvent.click(disabled);
    expect(server.playbackReads).toEqual([]);
  });

  it('plays a Song’s Selected Generation from Suno, by the Song’s choice', async () => {
    const { server, streamed } = catalog();
    server.song = {
      ...server.song,
      hasSelectedGeneration: true,
      selectedGeneration: {
        id: streamed.id,
        shortcode: G1,
        state: 'active',
        remoteState: 'present',
      },
      playback: { state: 'ready', reason: null },
    };
    server.songPlayback = {
      ...sunoPlayback(),
      generation: { id: streamed.id, shortcode: G1, sunoId: streamed.sunoId },
      state: 'ready',
      candidates: [],
    };
    renderApp('/songs/n8-7');
    await screen.findByRole('heading', { level: 2, name: baseSong.title });

    await userEvent.click(screen.getByTestId('song-play-button'));
    await waitFor(() => {
      expect(fake.element().paused).toBe(false);
    });

    const shown = await bar();
    expect(fake.element().getAttribute('src')).toBe(STREAM);
    expect(within(shown).getByTestId('player-source')).toHaveTextContent('Streaming from Suno');
    expect(within(shown).getByTestId('player-via')).toHaveTextContent(
      'The Song’s choice: its Selected Generation',
    );
  });
});
