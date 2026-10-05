import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { DEFAULT_INPUTS } from '../../test/createFieldsFixture';
import { renderApp } from '../../test/helpers';
import { testVersion, versionServer } from '../../test/versionServer';

const TEXT = { lyrics: '[Verse]\nRun with me\n', styles: 'punk, fast' };
const ONE = testVersion('1', { current: true, ...TEXT });

async function openVersion() {
  const user = userEvent.setup();
  renderApp('/songs/n8-7');
  await screen.findByRole('radiogroup', { name: 'Mode' });
  return user;
}

/** Waits for the autosave after the user's pause (1.5 s) to have sent `count` writes. */
async function writesReach(writes: unknown[], count: number, timeout = 4_000) {
  await waitFor(
    () => {
      expect(writes).toHaveLength(count);
    },
    { timeout },
  );
}

function indicator() {
  return within(screen.getByTestId('autosave')).getByRole('status');
}

describe('a Song’s options', () => {
  it('start as an Advanced Song: model, lyrics, styles, More Options collapsed, and Suno’s title', async () => {
    versionServer([ONE]);
    const user = await openVersion();

    const kind = screen.getByRole('radiogroup', { name: 'Kind' });
    expect(within(kind).getByRole('radio', { name: 'Song' })).toBeChecked();
    // Only Song can be chosen until the Speech and Sound story.
    expect(within(kind).getByRole('radio', { name: 'Speech' })).toBeDisabled();
    expect(within(kind).getByRole('radio', { name: 'Sound' })).toBeDisabled();
    expect(screen.getByRole('radio', { name: 'Advanced' })).toBeChecked();
    expect(screen.getByRole('combobox', { name: 'Model version' })).toHaveValue('');
    expect(screen.getByRole('textbox', { name: 'Lyrics' })).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Styles' })).toHaveValue(TEXT.styles);
    expect(screen.getByRole('textbox', { name: 'Song Title' })).toHaveValue('Running in a Pack');
    expect(screen.queryByRole('textbox', { name: 'Song description' })).not.toBeInTheDocument();

    const more = screen.getByRole('button', { name: 'More Options' });
    expect(more).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('slider', { name: 'Weirdness' })).not.toBeInTheDocument();

    await user.click(more);
    expect(more).toHaveAttribute('aria-expanded', 'true');
    const section = screen.getByTestId('more-options');
    expect(more).toHaveAttribute('aria-controls', section.id);
    // Suno's order, inside More Options.
    const order = [...section.querySelectorAll('[data-option]')].map((element) =>
      element.getAttribute('data-option'),
    );
    expect(order).toEqual([
      'excludeStyles',
      'vocalGender',
      'durationMode',
      'maxMode',
      'weirdness',
      'styleInfluence',
      'variety',
      'personalize',
    ]);
    // The model and Suno's title are outside it.
    expect(section).not.toContainElement(screen.getByRole('textbox', { name: 'Song Title' }));
    expect(section).not.toContainElement(screen.getByRole('combobox', { name: 'Model version' }));
  });

  it('save each change through the autosave as the options it changes', async () => {
    const { server } = versionServer([ONE]);
    const user = await openVersion();
    await user.click(screen.getByRole('button', { name: 'More Options' }));

    const weirdness = screen.getByRole('slider', { name: 'Weirdness' });
    weirdness.focus();
    await user.keyboard('{ArrowRight}{ArrowRight}');
    await user.click(screen.getByRole('radio', { name: 'Female' }));
    await user.click(screen.getByRole('radio', { name: 'Custom' }));
    const duration = screen.getByRole('slider', { name: 'Custom duration' });
    expect(duration).toHaveAttribute('aria-valuetext', '3 minutes');
    await user.selectOptions(screen.getByRole('combobox', { name: 'Model version' }), 'v6');

    await writesReach(server.writes, 1);
    expect(server.writes[0]?.body).toEqual({
      inputs: { weirdness: 52, vocalGender: 'female', durationMode: 'custom', model: 'v6' },
    });
    await waitFor(() => {
      expect(indicator()).toHaveTextContent('Saved');
    });
    expect(server.versions[0]?.inputs).toMatchObject({ weirdness: 52, model: 'v6' });
    expect(server.versions[0]?.lyrics).toBe(TEXT.lyrics);
  });

  it('switch to Simple and back, hiding what does not apply and keeping every value', async () => {
    const { server } = versionServer([
      testVersion('1', { current: true, ...TEXT, inputs: { ...DEFAULT_INPUTS, weirdness: 80 } }),
    ]);
    const user = await openVersion();

    await user.click(screen.getByRole('radio', { name: 'Simple' }));
    expect(screen.getByRole('textbox', { name: 'Song description' })).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Model version' })).toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Lyrics' })).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Styles' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'More Options' })).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Song Title' })).not.toBeInTheDocument();

    await user.type(screen.getByRole('textbox', { name: 'Song description' }), 'A night drive');
    await writesReach(server.writes, 1);
    expect(server.writes[0]?.body).toEqual({
      inputs: { songMode: 'simple', simplePrompt: 'A night drive' },
    });

    await user.click(screen.getByRole('radio', { name: 'Advanced' }));
    expect(screen.queryByRole('textbox', { name: 'Song description' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'More Options' }));
    expect(screen.getByRole('slider', { name: 'Weirdness' })).toHaveAttribute(
      'aria-valuenow',
      '80',
    );
    expect(screen.getByRole('textbox', { name: 'Song Title' })).toHaveValue('Running in a Pack');

    await user.click(screen.getByRole('radio', { name: 'Simple' }));
    expect(screen.getByRole('textbox', { name: 'Song description' })).toHaveValue('A night drive');
  });

  it('in Simple mode add and remove the lyrics and styles sections, which hold Advanced’s text', async () => {
    const { server } = versionServer([
      testVersion('1', {
        current: true,
        ...TEXT,
        inputs: { ...DEFAULT_INPUTS, songMode: 'simple' },
      }),
    ]);
    const user = await openVersion();

    expect(screen.queryByRole('textbox', { name: 'Lyrics' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Add lyrics' }));
    expect(screen.getByRole('textbox', { name: 'Lyrics' })).toHaveTextContent('Run with me');
    expect(screen.queryByRole('textbox', { name: 'Styles' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Add styles' }));
    expect(screen.getByRole('textbox', { name: 'Styles' })).toHaveValue(TEXT.styles);
    await writesReach(server.writes, 1);
    expect(server.writes[0]?.body).toEqual({
      inputs: { simpleLyricsAdded: true, simpleStylesAdded: true },
    });

    await user.click(screen.getByRole('button', { name: 'Remove lyrics' }));
    expect(screen.queryByTestId('lyrics-editor')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add lyrics' })).toBeVisible();
    await writesReach(server.writes, 2);
    expect(server.writes[1]?.body).toEqual({ inputs: { simpleLyricsAdded: false } });
  });

  it('complement: with the lyrics section removed, the lyrics editor is not drawn but the text is still the Version’s', async () => {
    const { server } = versionServer([
      testVersion('1', {
        current: true,
        ...TEXT,
        inputs: { ...DEFAULT_INPUTS, songMode: 'simple', simpleLyricsAdded: false },
      }),
    ]);
    const user = await openVersion();

    expect(screen.queryByTestId('lyrics-editor')).not.toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: 'Song description' }), 'x');
    await writesReach(server.writes, 1);
    expect(server.writes[0]?.body).not.toHaveProperty('lyrics');
    expect(server.versions[0]?.lyrics).toBe(TEXT.lyrics);

    // Advanced shows the same text.
    await user.click(screen.getByRole('radio', { name: 'Advanced' }));
    expect(screen.getByRole('textbox', { name: 'Lyrics' })).toHaveTextContent('Run with me');
  });

  it('are all read only on a frozen Version', async () => {
    const { server } = versionServer([
      testVersion('1', {
        current: true,
        isFrozen: true,
        ...TEXT,
        inputs: { ...DEFAULT_INPUTS, vocalGender: 'male', maxMode: true },
      }),
    ]);
    const user = await openVersion();
    await user.click(screen.getByRole('button', { name: 'More Options' }));

    expect(screen.getByRole('combobox', { name: 'Model version' })).toBeDisabled();
    expect(screen.getByRole('textbox', { name: 'Song Title' })).toHaveAttribute('readonly');
    expect(screen.getByRole('textbox', { name: 'Exclude styles' })).toHaveAttribute('readonly');
    expect(screen.getByRole('switch', { name: 'Max Mode' })).toBeDisabled();
    expect(screen.getByRole('switch', { name: 'Personalize (My Taste)' })).toBeDisabled();
    for (const name of ['Weirdness', 'Style Influence', 'Variety']) {
      expect(screen.getByRole('slider', { name })).toHaveAttribute('aria-disabled', 'true');
    }

    await user.click(screen.getByRole('radio', { name: 'Female' }));
    await user.click(screen.getByRole('radio', { name: 'Simple' }));
    await user.type(screen.getByRole('textbox', { name: 'Song Title' }), 'more');
    expect(screen.getByRole('radio', { name: 'Male' })).toBeChecked();
    expect(screen.getByRole('radio', { name: 'Advanced' })).toBeChecked();

    // Nothing is sent once the autosave's pause has passed.
    await new Promise((resolve) => setTimeout(resolve, 1_700));
    expect(server.writes).toHaveLength(0);
  });
});
