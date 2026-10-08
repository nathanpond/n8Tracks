import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { UnmatchedFile } from '../api/audioFiles';
import type { Generation } from '../api/generations';
import { installFakeAudio, type FakeAudio } from '../test/fakeAudio';
import { renderApp, requestPath } from '../test/helpers';
import {
  testGeneration,
  testSongAudioFile,
  testVersion,
  versionServer,
} from '../test/versionServer';

/**
 * Compare (#220) in the player bar, over a fake audio element: the menu lists the Song's sources and
 * switching to one carries the time over (from the start when it is shorter); switching while
 * playing keeps playing and while paused stays paused; Previous and Next wrap; A/B alternates
 * between the last two played; a switch that fails goes back; the latest switch wins; shortcuts
 * work but never in a text field and are in each control's description; the bar rates the playing
 * Generation; and switching sends no write.
 */

const ONE = testVersion('1', { current: true, isFrozen: true });
const PLAYABLE = { playable: true, reason: null };
const G1 = 'n8-7-v1-g1';
const G2 = 'n8-7-v1-g2';
const G3 = 'n8-7-v1-g3';

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

function sourceGeneration(
  generation: Generation,
  files: UnmatchedFile[],
  rating: number | null = null,
) {
  return {
    generation: { id: generation.id, shortcode: generation.shortcode },
    versionNumber: '1',
    rating,
    revision: generation.revision,
    durationSeconds: 125,
    state: 'active',
    remoteState: 'present',
    files: files.map((file, index) => ({
      audioFile: playbackFile(file),
      isPlaybackFile: index === 0,
    })),
  };
}

/** The Song with three playable Generations (G3 has a WAV and an MP3) and a Song-level file. */
function catalog() {
  const { server, mock } = versionServer([ONE]);
  const generations = [1, 2, 3].map((ordinal) =>
    testGeneration('1', ordinal, {
      playback: PLAYABLE,
      audioFiles: { count: 1, missing: 0, unavailable: 0, formats: ['wav'] },
    }),
  );
  const [first, second, third] = generations as [Generation, Generation, Generation];
  const firstFile = testSongAudioFile('Take one.wav', first, { playsForGeneration: true });
  const secondFile = testSongAudioFile('Take two.wav', second, { playsForGeneration: true });
  const thirdFile = testSongAudioFile('Take three.wav', third, { playsForGeneration: true });
  const thirdMp3 = testSongAudioFile('Take three.mp3', third);
  const master = testSongAudioFile('Master.wav', null);
  server.generations = generations;
  server.audioFiles = [master, firstFile, secondFile, thirdFile, thirdMp3];
  for (const [generation, file] of [
    [first, firstFile],
    [second, secondFile],
    [third, thirdFile],
  ] as const) {
    server.playback.set(generation.id, {
      source: 'local',
      audioFile: playbackFile(file),
      reason: 'format_order',
    });
  }
  server.playbackSources = {
    song: { id: server.song.id, shortcode: server.song.shortcode, title: server.song.title },
    songFiles: [{ audioFile: playbackFile(master), isPlaybackFile: false }],
    generations: [
      sourceGeneration(first, [firstFile], 4),
      sourceGeneration(second, [secondFile]),
      sourceGeneration(third, [thirdFile, thirdMp3]),
    ],
  };
  return { server, mock, first, firstFile, secondFile, thirdFile, thirdMp3, master };
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

async function playFirst(at = 30) {
  const row = document.querySelector(`tr[data-generation="${G1}"]`);
  if (!(row instanceof HTMLElement)) {
    throw new Error('G1 is not listed.');
  }
  await userEvent.click(within(row).getByRole('button', { name: `Play ${G1}` }));
  await waitFor(() => {
    expect(fake.element().paused).toBe(false);
  });
  fake.loaded(125);
  fake.advance(at);
  // The bar has read the Song's sources.
  await screen.findByRole('button', { name: `Compare: playing ${G1} · WAV` });
}

function src(): string {
  return fake.element().getAttribute('src') ?? '';
}

function detail(): HTMLElement {
  return screen.getByTestId('player-detail');
}

function control(name: 'Previous' | 'Next' | 'A/B'): HTMLElement {
  return within(screen.getByTestId('player-compare')).getByRole('button', { name });
}

function press(code: string, target: Element = document.body) {
  fireEvent.keyDown(target, { code, key: code });
}

/** Every request since `from` that was not a read. */
function writesSince(mock: ReturnType<typeof catalog>['mock'], from: number) {
  return mock.mock.calls
    .slice(from)
    .map(([input, init]) => ({ path: requestPath(input), method: init?.method ?? 'GET' }))
    .filter((request) => request.method !== 'GET');
}

describe('Compare', () => {
  it('lists the Song’s sources and switches while playing at the same moment, from the start when shorter', async () => {
    const { mock, secondFile, thirdFile, master, firstFile } = catalog();
    await openSong();
    await playFirst();
    const before = mock.mock.calls.length;

    await userEvent.click(screen.getByTestId('compare-button'));
    const menu = await screen.findByRole('menu');
    expect(
      within(menu)
        .getAllByRole('menuitem')
        .map((item) => item.textContent.replace('▶', '')),
    ).toEqual([
      'Master.wav',
      `${G1} · WAV (playing now)`,
      `${G2} · WAV`,
      `${G3} · WAV`,
      `${G3} · MP3`,
    ]);
    expect(menu).toHaveTextContent(`Version 1 · ${G1} · 4 stars`);
    expect(menu).toHaveTextContent(`Version 1 · ${G3} · not rated`);
    await userEvent.click(within(menu).getByRole('menuitem', { name: `${G2} · WAV` }));

    // Loaded, sought, then started: at 0:30, still playing, and the bar names it.
    expect(src()).toContain(secondFile.id);
    expect(screen.getByTestId('player-bar')).toHaveAttribute('data-status', 'loading');
    fake.loaded(125);
    expect(fake.time()).toBe(30);
    expect(fake.element().paused).toBe(false);
    expect(detail()).toHaveTextContent(`Version 1 · ${G2} · WAV`);
    expect(screen.getByTestId('player-toggle')).toHaveTextContent('Pause');
    expect(screen.getByTestId('compare-announcement')).toHaveTextContent(
      `Now playing ${G2} · WAV.`,
    );

    // Next: G3, whose audio is shorter than 0:30: it starts from its beginning.
    await userEvent.click(control('Next'));
    expect(src()).toContain(thirdFile.id);
    fake.loaded(20);
    expect(fake.time()).toBe(0);
    expect(fake.element().paused).toBe(false);

    // Next wraps to the Song-level file, then back to G1.
    fake.advance(10);
    await userEvent.click(control('Next'));
    expect(src()).toContain(master.id);
    fake.loaded(125);
    expect(fake.time()).toBe(10);
    expect(detail()).toHaveTextContent('Song-level file · Master.wav');
    await userEvent.click(control('Next'));
    expect(src()).toContain(firstFile.id);
    fake.loaded(125);
    await userEvent.click(control('Previous'));
    expect(src()).toContain(master.id);
    fake.loaded(125);

    // Nothing but reads was sent: no rating, selection, or preference changed.
    expect(writesSince(mock, before)).toEqual([]);
    expect(document.querySelectorAll('audio')).toHaveLength(1);
  });

  it('stays paused when switched while paused, and keeps the volume', async () => {
    const { secondFile } = catalog();
    await openSong();
    await playFirst();
    fireEvent.change(screen.getByRole('slider', { name: 'Volume' }), { target: { value: '40' } });
    await userEvent.click(screen.getByTestId('player-toggle'));
    expect(fake.element().paused).toBe(true);
    const plays = fake.plays();

    press('BracketRight');
    expect(src()).toContain(secondFile.id);
    expect(screen.getByTestId('player-bar')).toHaveAttribute('data-status', 'paused');
    fake.loaded(125);

    expect(fake.time()).toBe(30);
    expect(fake.element().paused).toBe(true);
    expect(fake.plays()).toBe(plays);
    expect(screen.getByTestId('player-toggle')).toHaveTextContent('Play');
    expect(fake.element().volume).toBeCloseTo(0.4);
  });

  it('alternates A/B between the last two played at the same moment', async () => {
    const { mock, firstFile, secondFile } = catalog();
    await openSong();
    await playFirst();
    const before = mock.mock.calls.length;
    expect(control('A/B')).toBeDisabled();

    press('BracketRight');
    fake.loaded(125);
    fake.advance(5);
    expect(control('A/B')).toBeEnabled();
    expect(control('A/B')).toHaveAccessibleDescription(
      `Switches to ${G1} · WAV, at the same moment. Shortcut: \\ (backslash).`,
    );

    press('Backslash');
    expect(src()).toContain(firstFile.id);
    fake.loaded(125);
    expect(fake.time()).toBe(35);
    expect(fake.element().paused).toBe(false);

    press('Backslash');
    expect(src()).toContain(secondFile.id);
    fake.loaded(125);
    expect(fake.time()).toBe(35);

    // Auto-repeat and modifiers do nothing.
    fireEvent.keyDown(document.body, { code: 'Backslash', repeat: true });
    fireEvent.keyDown(document.body, { code: 'Backslash', ctrlKey: true });
    expect(src()).toContain(secondFile.id);
    expect(writesSince(mock, before)).toEqual([]);
  });

  it('goes back to the source before a switch that fails, and keeps it out of A/B', async () => {
    const { firstFile, secondFile } = catalog();
    await openSong();
    await playFirst();

    press('BracketRight');
    expect(src()).toContain(secondFile.id);
    fake.fail();
    expect(src()).toContain(firstFile.id);
    fake.loaded(125);

    expect(fake.time()).toBe(30);
    expect(fake.element().paused).toBe(false);
    expect(screen.getByTestId('player-notice')).toHaveTextContent(
      `${G2} · WAV could not be played, so the player went back to ${G1} · WAV.`,
    );
    expect(screen.queryByTestId('player-error')).toBeNull();
    expect(control('A/B')).toBeDisabled();

    // A seek that fails is a failed switch too.
    press('BracketRight');
    fake.failNextSeek();
    fake.loaded(125);
    expect(src()).toContain(firstFile.id);
    fake.loaded(125);
    expect(fake.time()).toBe(30);
    expect(control('A/B')).toBeDisabled();
  });

  it('lets the latest switch win, carrying the time of the source that actually played', async () => {
    const { thirdFile } = catalog();
    await openSong();
    await playFirst();

    press('BracketRight');
    press('BracketRight');
    expect(src()).toContain(thirdFile.id);
    fake.loaded(125);

    expect(fake.time()).toBe(30);
    expect(fake.element().paused).toBe(false);
    expect(control('A/B')).toHaveAccessibleDescription(
      `Switches to ${G1} · WAV, at the same moment. Shortcut: \\ (backslash).`,
    );
  });

  it('lists each shortcut in its control’s description and never takes one from a text field', async () => {
    const { firstFile } = catalog();
    await openSong();
    await playFirst();

    expect(control('Previous')).toHaveAccessibleDescription(
      'Previous source of this Song. Shortcut: [ (left bracket).',
    );
    expect(control('Next')).toHaveAccessibleDescription(
      'Next source of this Song. Shortcut: ] (right bracket).',
    );
    const goTo = screen.getByRole('textbox', { name: 'Go to' });
    goTo.focus();
    press('BracketRight', goTo);
    press('BracketLeft', goTo);
    expect(src()).toContain(firstFile.id);
  });

  it('rates the playing Generation from the bar without stopping, and has no rating for a Song-level file', async () => {
    const { server, mock, first } = catalog();
    await openSong();
    await playFirst();
    const before = mock.mock.calls.length;

    const rating = within(screen.getByTestId('player-rating')).getByRole('radiogroup', {
      name: `Rating of ${G1}`,
    });
    expect(within(rating).getByRole('radio', { name: '4 stars' })).toBeChecked();
    await userEvent.click(within(rating).getByRole('radio', { name: '2 stars' }));

    await waitFor(() => {
      expect(server.generations.find((generation) => generation.id === first.id)?.rating).toBe(2);
    });
    expect(writesSince(mock, before)).toEqual([
      { path: `/api/v1/generations/${first.id}`, method: 'PATCH' },
    ]);
    expect(fake.element().paused).toBe(false);
    expect(within(rating).getByRole('radio', { name: '2 stars' })).toBeChecked();

    press('BracketLeft');
    fake.loaded(125);
    expect(detail()).toHaveTextContent('Song-level file · Master.wav');
    expect(screen.queryByTestId('player-rating')).toBeNull();
  });
});
