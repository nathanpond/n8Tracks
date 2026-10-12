import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { Generation } from '../api/generations';
import type { SourceSettingsPlan } from '../api/sourceSettings';
import type { VersionDetail } from '../api/versions';
import { DEFAULT_INPUTS } from '../test/createFieldsFixture';
import { fakeTimeouts } from '../test/fakeClock';
import { renderApp } from '../test/helpers';
import { SONG_B } from '../test/songServer';
import { testGeneration, testVersion, versionServer } from '../test/versionServer';

const LONG_LYRICS = Array.from({ length: 40 }, (_, line) => `Source line ${String(line + 1)}`).join(
  '\n',
);

/** Generation 1 of Song B's Version 1: the source chosen. */
function sourceGeneration(): Generation {
  return testGeneration('1', 1, {
    id: '0199b1a0-6100-7000-9000-000008000001',
    shortcode: 'n8-8-v1-g1',
    song: { id: SONG_B.id, shortcode: SONG_B.shortcode },
    version: { id: `${SONG_B.id}-v1`, shortcode: 'n8-8-v1' },
    sunoId: 'suno-n8-8-1',
    title: 'Song B take 1',
  });
}

/** A fake n8Tracks whose current Version holds `change`, and whose source settings plan is `plan`. */
function serve(
  plan: Pick<SourceSettingsPlan, 'fills' | 'conflicts' | 'unknown'>,
  change: Partial<VersionDetail> = {},
) {
  const fake = versionServer([
    testVersion('1', { current: true, inputs: { ...DEFAULT_INPUTS }, ...change }),
  ]);
  fake.server.otherSongs = [SONG_B];
  fake.server.otherGenerations = [sourceGeneration()];
  fake.server.sourceSettings = () => plan;
  return fake;
}

async function open() {
  const { advanceTimers } = fakeTimeouts();
  const user = userEvent.setup({ advanceTimers });
  renderApp('/songs/n8-7');
  await screen.findByRole('heading', { name: 'Sources' });
  await waitFor(() => {
    expect(screen.getByRole('combobox', { name: 'Action' })).toBeEnabled();
  });
  return user;
}

/** Chooses `action`, then Song B's first Generation as its source. */
async function chooseSource(user: ReturnType<typeof userEvent.setup>, action: string) {
  await user.selectOptions(screen.getByRole('combobox', { name: 'Action' }), action);
  await user.click(screen.getByRole('button', { name: `Choose the ${action} source` }));
  const picker = await screen.findByRole('dialog', { name: `Choose the ${action} source` });
  await user.type(within(picker).getByRole('textbox', { name: 'Song' }), 'Song B');
  await user.click(await within(picker).findByRole('option', { name: /Song B/ }));
  await user.click(await within(picker).findByRole('button', { name: 'Choose n8-8-v1-g1' }));
}

function section() {
  return screen.getByTestId('sources-section');
}

const CONFLICTS: SourceSettingsPlan['conflicts'] = [
  { key: 'lyrics', versionValue: 'My own lyrics', sourceValue: LONG_LYRICS },
  { key: 'weirdness', versionValue: 50, sourceValue: 70 },
  { key: 'vocalGender', versionValue: 'male', sourceValue: 'female' },
];

describe('a source’s settings come with a Cover or Extend source (#431)', () => {
  it('fills the empty settings without asking when none differ, and says what it filled', async () => {
    const { server } = serve({
      fills: [
        { key: 'lyrics', sourceValue: 'From the source' },
        { key: 'voice', sourceValue: { personaId: 'persona-1', name: 'Smoky' } },
      ],
      conflicts: [],
      unknown: [],
    });
    const user = await open();

    await chooseSource(user, 'Cover');

    expect(await screen.findByText('Filled from the source: Lyrics, Voice.')).toBeInTheDocument();
    expect(screen.queryByRole('dialog', { name: 'Settings from the source' })).toBeNull();
    expect(within(section()).getByTestId('source-title')).toHaveTextContent('Song B take 1');
    expect(server.versions[0]?.lyrics).toBe('From the source');
    expect(server.writes.at(-1)?.body).toMatchObject({ take: [] });
    expect(server.settingsPreviews).toHaveLength(1);
  });

  it('asks once about the differing settings, side by side, and takes only the ticked ones', async () => {
    const { server } = serve(
      {
        fills: [{ key: 'excludeStyles', sourceValue: 'no drums' }],
        conflicts: CONFLICTS,
        unknown: [{ key: 'styles', reason: 'not_returned' }],
      },
      { lyrics: 'My own lyrics' },
    );
    const user = await open();

    await chooseSource(user, 'Cover');
    const prompt = await screen.findByRole('dialog', { name: 'Settings from the source' });

    // Each differing setting is a group with both values and one tick, unticked.
    const lyrics = within(prompt).getByRole('group', { name: 'Lyrics' });
    const sourceLyrics = within(lyrics).getByRole('region', { name: 'Lyrics: the source’s value' });
    expect(sourceLyrics.textContent).toBe(LONG_LYRICS);
    expect(sourceLyrics).toHaveAttribute('tabindex', '0');
    expect(
      within(lyrics).getByRole('region', { name: 'Lyrics: this Version’s value' }),
    ).toHaveTextContent('My own lyrics');
    const weirdness = within(prompt).getByRole('group', { name: 'Weirdness' });
    expect(weirdness).toHaveTextContent('50');
    expect(weirdness).toHaveTextContent('70');
    expect(within(prompt).getByRole('group', { name: 'Vocal Gender' })).toHaveTextContent('Female');
    for (const name of ['Lyrics', 'Weirdness', 'Vocal Gender']) {
      expect(
        within(prompt).getByRole('checkbox', { name: `Use the source’s ${name}` }),
      ).not.toBeChecked();
    }
    expect(within(prompt).getByTestId('source-settings-intro')).toHaveTextContent(
      'Its empty settings are filled from the source: Exclude styles.',
    );
    expect(within(prompt).getByTestId('source-settings-unknown')).toHaveTextContent(
      'Suno did not return the source’s Styles',
    );
    // Nothing is written while the prompt is open.
    expect(server.writes).toHaveLength(0);

    await user.click(within(prompt).getByRole('checkbox', { name: 'Use the source’s Weirdness' }));
    await user.click(within(prompt).getByRole('button', { name: 'Confirm' }));

    await waitFor(
      () => {
        expect(screen.queryByRole('dialog', { name: 'Settings from the source' })).toBeNull();
      },
      { timeout: 3_000 },
    );
    expect(server.writes).toHaveLength(1);
    expect(server.writes[0]?.body).toMatchObject({ take: ['weirdness'] });
    const stored = server.versions[0];
    expect(stored?.inputs.weirdness).toBe(70);
    expect(stored?.inputs.vocalGender).toBe(DEFAULT_INPUTS.vocalGender);
    expect(stored?.inputs.excludeStyles).toBe('no drums');
    expect(stored?.lyrics).toBe('My own lyrics');
    expect(await within(section()).findByTestId('source-title')).toHaveTextContent('Song B take 1');
  });

  it('cancelling changes nothing: no source, no setting, and nothing written', async () => {
    const { server } = serve({
      fills: [{ key: 'styles', sourceValue: 'x' }],
      conflicts: CONFLICTS,
      unknown: [],
    });
    const user = await open();

    await chooseSource(user, 'Cover');
    const prompt = await screen.findByRole('dialog', { name: 'Settings from the source' });
    await user.click(within(prompt).getByRole('checkbox', { name: 'Use the source’s Lyrics' }));
    await user.click(within(prompt).getByRole('button', { name: 'Cancel' }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Settings from the source' })).toBeNull();
    });
    expect(within(section()).queryByRole('list', { name: 'Audio sources' })).toBeNull();
    expect(server.writes).toHaveLength(0);
    expect(server.versions[0]?.inputs.sources).toEqual(DEFAULT_INPUTS.sources);
    expect(server.versions[0]?.styles).toBe('');
  });

  it('Escape cancels the prompt too', async () => {
    const { server } = serve({ fills: [], conflicts: CONFLICTS, unknown: [] });
    const user = await open();

    await chooseSource(user, 'Extend');
    await screen.findByRole('dialog', { name: 'Settings from the source' });
    await user.keyboard('{Escape}');

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Settings from the source' })).toBeNull();
    });
    expect(server.writes).toHaveLength(0);
  });

  it('says when nothing about the source is known', async () => {
    serve({
      fills: [],
      conflicts: [],
      unknown: [
        { key: 'lyrics', reason: 'not_imported' },
        { key: 'styles', reason: 'not_imported' },
      ],
    });
    const user = await open();

    await chooseSource(user, 'Cover');

    expect(
      await screen.findByText(
        'No settings were taken from the source: n8Tracks does not know the settings of a Suno clip it has not imported.',
      ),
    ).toBeInTheDocument();
  });

  it('other source actions are unchanged: no settings are read, and autosave saves the source', async () => {
    const { server } = serve({
      fills: [{ key: 'lyrics', sourceValue: 'Never' }],
      conflicts: CONFLICTS,
      unknown: [],
    });
    const user = await open();

    await chooseSource(user, 'Sample This Song');

    await waitFor(
      () => {
        expect(server.writes).toHaveLength(1);
      },
      { timeout: 4_000 },
    );
    expect(server.writes[0]?.method).toBe('PATCH');
    expect(server.settingsPreviews).toHaveLength(0);
    expect(screen.queryByRole('dialog', { name: 'Settings from the source' })).toBeNull();
    expect(server.versions[0]?.lyrics).toBe('');
  });
});
