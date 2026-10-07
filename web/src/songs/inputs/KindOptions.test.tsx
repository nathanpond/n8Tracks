import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { DEFAULT_INPUTS } from '../../test/createFieldsFixture';
import { fakeTimeouts } from '../../test/fakeClock';
import { renderApp } from '../../test/helpers';
import { testVersion, versionServer } from '../../test/versionServer';

const TEXT = { lyrics: '[Verse]\nRun with me\n', styles: 'punk, fast' };

/**
 * Opens the Song's current Version. Autosave's pause runs on the fake clock (fakeTimeouts), so a
 * loaded machine spends no real time waiting for it and cannot run the test's timeout out.
 */
async function openVersion() {
  const { advanceTimers } = fakeTimeouts();
  const user = userEvent.setup({ advanceTimers });
  renderApp('/songs/n8-7');
  await screen.findByRole('radiogroup', { name: 'Kind' });
  return user;
}

/**
 * Waits for the autosave after the user's pause (1.5 s) to have sent `count` writes, within
 * `timeout` on the fake clock.
 */
async function writesReach(writes: unknown[], count: number, timeout = 4_000) {
  await waitFor(
    () => {
      expect(writes).toHaveLength(count);
    },
    { timeout },
  );
}

function kindRadio(name: string) {
  return within(screen.getByRole('radiogroup', { name: 'Kind' })).getByRole('radio', { name });
}

describe('a Speech’s and a Sound’s options', () => {
  it('replace the Song’s when the kind changes to Speech: Script, Tone, and the Advanced options; no lyrics, styles, or History', async () => {
    const { server } = versionServer([testVersion('1', { current: true, ...TEXT })]);
    const user = await openVersion();
    expect(screen.getByRole('button', { name: 'Show history' })).toBeVisible();
    expect(screen.getByTestId('song-kind')).toHaveTextContent('Kind: Song');

    await user.click(kindRadio('Speech'));

    expect(screen.getByRole('textbox', { name: 'Script' })).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Tone' })).toBeInTheDocument();
    expect(screen.getByRole('radiogroup', { name: 'Vocal Gender' })).toBeInTheDocument();
    expect(screen.getByRole('switch', { name: 'Background music' })).toBeChecked();
    expect(screen.getByRole('slider', { name: 'Variety' })).toHaveAttribute(
      'aria-valuetext',
      'Normal',
    );
    expect(screen.getByRole('radio', { name: 'Advanced' })).toBeChecked();
    // Speech has no model, and the Song's options, lyrics, styles, and History go.
    expect(screen.queryByRole('combobox', { name: 'Model version' })).not.toBeInTheDocument();
    expect(screen.queryByTestId('lyrics-editor')).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Styles' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'More Options' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Show history' })).not.toBeInTheDocument();

    await user.type(screen.getByRole('textbox', { name: 'Script' }), 'Hello there');
    await user.click(screen.getByRole('radio', { name: 'Female' }));
    await writesReach(server.writes, 1);
    expect(server.writes[0]?.body).toEqual({
      inputs: { kind: 'speech', speechScript: 'Hello there', speechVocalGender: 'female' },
    });
    // The Song's Vocal Gender is its own.
    expect(server.versions[0]?.inputs).toMatchObject({ vocalGender: null });
    // The Song page says the current Version is now a Speech.
    await waitFor(() => {
      expect(screen.getByTestId('song-kind')).toHaveTextContent('Kind: Speech');
    });
  });

  it('in Simple mode offer one Speech description of up to 1,000 characters', async () => {
    const { server } = versionServer([
      testVersion('1', { current: true, inputs: { ...DEFAULT_INPUTS, kind: 'speech' } }),
    ]);
    const user = await openVersion();

    await user.click(screen.getByRole('radio', { name: 'Simple' }));
    const prompt = screen.getByRole('textbox', { name: 'Speech description' });
    expect(prompt).toHaveAccessibleDescription(/0 \/ 1,000 characters/);
    expect(screen.queryByRole('textbox', { name: 'Script' })).not.toBeInTheDocument();
    await user.type(prompt, 'A calm narrator');
    await writesReach(server.writes, 1);
    expect(server.writes[0]?.body).toEqual({
      inputs: { speechMode: 'simple', speechPrompt: 'A calm narrator' },
    });
    // The Song's mode is untouched.
    expect(server.versions[0]?.inputs).toMatchObject({ songMode: 'advanced' });
  });

  it('for a Sound offer a model, the description, Type, BPM, and Key, with a scale once a key is chosen', async () => {
    const { server } = versionServer([
      testVersion('1', { current: true, inputs: { ...DEFAULT_INPUTS, kind: 'sound' } }),
    ]);
    const user = await openVersion();

    // No Simple or Advanced switch, and no lyrics.
    expect(screen.queryByRole('radiogroup', { name: 'Mode' })).not.toBeInTheDocument();
    expect(screen.queryByTestId('lyrics-editor')).not.toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Model version' })).toHaveValue('');
    expect(screen.getByRole('radio', { name: 'One-Shot' })).toBeChecked();
    const bpm = screen.getByRole('textbox', { name: 'BPM' });
    expect(bpm).toHaveValue('');
    expect(bpm).toHaveAttribute('placeholder', 'Auto');
    const key = screen.getByRole('radiogroup', { name: 'Key' });
    expect(within(key).getAllByRole('radio')).toHaveLength(13);
    expect(within(key).getByRole('radio', { name: 'Any' })).toBeChecked();
    // No scale while the key is Any.
    expect(screen.queryByRole('radiogroup', { name: 'Key scale' })).not.toBeInTheDocument();

    await user.type(screen.getByRole('textbox', { name: 'Sound' }), 'Rain on a tin roof');
    await user.click(screen.getByRole('radio', { name: 'Loop' }));
    await user.type(bpm, '120');
    await user.click(within(key).getByRole('radio', { name: 'A' }));
    // Choosing a key chooses no scale.
    const scale = screen.getByRole('radiogroup', { name: 'Key scale' });
    expect(within(scale).getByRole('radio', { name: 'None' })).toBeChecked();
    await user.click(within(scale).getByRole('radio', { name: 'Minor' }));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Model version' }), 'v6-mini');

    await writesReach(server.writes, 1);
    expect(server.writes[0]?.body).toEqual({
      inputs: {
        soundDescription: 'Rain on a tin roof',
        soundType: 'loop',
        soundBpm: 120,
        soundKey: 'A',
        soundScale: 'minor',
        soundsModel: 'v6-mini',
      },
    });
    // A Sound's model is not a Song's.
    expect(server.versions[0]?.inputs).toMatchObject({ model: null });

    // Back to Any: the scale is hidden but kept; BPM emptied is Auto (null).
    await user.click(within(key).getByRole('radio', { name: 'Any' }));
    expect(screen.queryByRole('radiogroup', { name: 'Key scale' })).not.toBeInTheDocument();
    await user.clear(bpm);
    await writesReach(server.writes, 2);
    expect(server.writes[1]?.body).toEqual({ inputs: { soundKey: 'any', soundBpm: null } });
    expect(server.versions[0]?.inputs).toMatchObject({ soundScale: 'minor' });
  });

  it('keep every kind’s values: switching back shows the lyrics, then the script again', async () => {
    const { server } = versionServer([
      testVersion('1', {
        current: true,
        ...TEXT,
        inputs: { ...DEFAULT_INPUTS, speechScript: 'Once upon a time', soundDescription: 'Rain' },
      }),
    ]);
    const user = await openVersion();

    await user.click(kindRadio('Sound'));
    expect(screen.getByRole('textbox', { name: 'Sound' })).toHaveValue('Rain');
    await user.click(kindRadio('Song'));
    expect(screen.getByRole('textbox', { name: 'Lyrics' })).toHaveTextContent('Run with me');
    await user.click(kindRadio('Speech'));
    expect(screen.getByRole('textbox', { name: 'Script' })).toHaveValue('Once upon a time');

    await writesReach(server.writes, 1);
    expect(server.writes[0]?.body).toEqual({ inputs: { kind: 'speech' } });
    expect(server.versions[0]?.lyrics).toBe(TEXT.lyrics);
  });

  it('are read only on a frozen Version', async () => {
    const { server } = versionServer([
      testVersion('1', {
        current: true,
        isFrozen: true,
        inputs: { ...DEFAULT_INPUTS, kind: 'sound', soundKey: 'C#', soundScale: 'major' },
      }),
    ]);
    const user = await openVersion();

    expect(screen.getByRole('textbox', { name: 'Sound' })).toHaveAttribute('readonly');
    expect(screen.getByRole('textbox', { name: 'BPM' })).toHaveAttribute('readonly');
    expect(screen.getByRole('combobox', { name: 'Model version' })).toBeDisabled();
    const key = screen.getByRole('radiogroup', { name: 'Key' });
    for (const radio of within(key).getAllByRole('radio')) {
      expect(radio).toBeDisabled();
    }
    await user.click(screen.getByRole('radio', { name: 'Loop' }));
    await user.click(kindRadio('Speech'));
    expect(screen.getByRole('radio', { name: 'One-Shot' })).toBeChecked();
    expect(within(key).getByRole('radio', { name: 'C#' })).toBeChecked();

    // Past the autosave pause (1.5 s): nothing is sent.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1_700);
    });
    expect(server.writes).toHaveLength(0);
  });

  it('a frozen Speech is read only too', async () => {
    versionServer([
      testVersion('1', {
        current: true,
        isFrozen: true,
        inputs: { ...DEFAULT_INPUTS, kind: 'speech', speechScript: 'Kept' },
      }),
    ]);
    await openVersion();

    expect(screen.getByRole('textbox', { name: 'Script' })).toHaveAttribute('readonly');
    expect(screen.getByRole('textbox', { name: 'Tone' })).toHaveAttribute('readonly');
    expect(screen.getByRole('switch', { name: 'Background music' })).toBeDisabled();
    expect(screen.getByRole('slider', { name: 'Variety' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    expect(screen.getByTestId('song-kind')).toHaveTextContent('Kind: Speech');
  });
});
