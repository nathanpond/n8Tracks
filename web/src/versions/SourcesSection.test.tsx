import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { Generation } from '../api/generations';
import type { LineageSource } from '../api/lineage';
import type { VersionDetail } from '../api/versions';
import { DEFAULT_INPUTS } from '../test/createFieldsFixture';
import { fakeTimeouts } from '../test/fakeClock';
import { renderApp } from '../test/helpers';
import { relationshipType, SONG_B, SONG_C, SYSTEM_TYPES } from '../test/songServer';
import { testGeneration, testVersion, versionServer } from '../test/versionServer';

const typeId = (name: string) => SYSTEM_TYPES.find((type) => type.name === name)?.id ?? '';

/** Generation `ordinal` of Version 1 of `song`, with a title and a length. */
function otherGeneration(
  song: typeof SONG_B,
  ordinal: number,
  change: Partial<Generation> = {},
): Generation {
  const version = `${song.shortcode}-v1`;
  return testGeneration('1', ordinal, {
    id: `0199b1a0-6100-7000-9000-${song.shortcode.replace('n8-', '').padStart(6, '0')}${String(ordinal).padStart(6, '0')}`,
    shortcode: `${version}-g${String(ordinal)}`,
    song: { id: song.id, shortcode: song.shortcode },
    version: { id: `${song.id}-v1`, shortcode: version },
    sunoId: `suno-${song.shortcode}-${String(ordinal)}`,
    title: `${song.title} take ${String(ordinal)}`,
    ...change,
  });
}

/** A source as the API reads it back, pointing at `generation`. */
function readSource(generation: Generation, change: Partial<LineageSource> = {}): LineageSource {
  return {
    generation: {
      id: generation.id,
      shortcode: generation.shortcode,
      songId: generation.song.id,
      songShortcode: generation.song.shortcode,
      songTitle: 'Song B',
      title: generation.title,
      durationSeconds: generation.durationSeconds,
      missing: false,
    },
    availability: 'ok',
    ...change,
  };
}

/** A fake n8Tracks whose current Version has `inputs` (merged over the defaults), with other Songs to choose from. */
function serve(inputs: Record<string, unknown> = {}, change: Partial<VersionDetail> = {}) {
  const fake = versionServer([
    testVersion('1', {
      current: true,
      inputs: { ...DEFAULT_INPUTS, ...(inputs as VersionDetail['inputs']) },
      ...change,
    }),
  ]);
  fake.server.otherSongs = [SONG_B, SONG_C];
  fake.server.otherGenerations = [
    otherGeneration(SONG_B, 1, { durationSeconds: 125 }),
    otherGeneration(SONG_B, 2, {
      durationSeconds: null,
      remoteState: 'trashed',
      state: 'archived',
    }),
    otherGeneration(SONG_B, 3),
    otherGeneration(SONG_C, 1),
    otherGeneration(SONG_C, 2),
  ];
  return fake;
}

async function openVersion() {
  return (await openVersionOnClock()).user;
}

/** As {@link openVersion}, with the fake clock it put on. */
async function openVersionOnClock() {
  const clock = fakeTimeouts();
  const user = userEvent.setup({ advanceTimers: clock.advanceTimers });
  renderApp('/songs/n8-7');
  await screen.findByRole('heading', { name: 'Sources' });
  await waitFor(() => {
    expect(screen.getByRole('combobox', { name: 'Action' })).toBeEnabled();
  });
  return { user, clock };
}

/** Opens the Song's current Version when it is frozen (its Action is text, not a control). */
async function openFrozen() {
  const { advanceTimers } = fakeTimeouts();
  const user = userEvent.setup({ advanceTimers });
  renderApp('/songs/n8-7');
  await screen.findByTestId('sources-frozen');
  return user;
}

function section() {
  return screen.getByTestId('sources-section');
}

/** Waits for autosave to have sent `count` writes and answers the last one's body. */
async function lastWrite(writes: { body: Record<string, unknown> }[], count: number) {
  await waitFor(
    () => {
      expect(writes).toHaveLength(count);
    },
    { timeout: 4_000 },
  );
  return writes[count - 1]?.body;
}

async function openedDialog(name: string | RegExp) {
  // Found by its role and name; its opening transition runs on animation frames, which the fake
  // clock does not move, so it is not waited for: user-event acts on it either way.
  return screen.findByRole('dialog', { name });
}

/** In the open picker: searches for `song`, then chooses its Generation `shortcode`. */
async function pick(user: ReturnType<typeof userEvent.setup>, song: string, shortcode: string) {
  const dialog = await openedDialog(/^Choose/);
  await user.type(within(dialog).getByRole('textbox', { name: 'Song' }), song);
  await user.click(await within(dialog).findByRole('option', { name: new RegExp(song) }));
  await user.click(await within(dialog).findByRole('button', { name: `Choose ${shortcode}` }));
  await closed();
}

/** Waits until the fake API holds audio sources pointing at the Generations `ids`, in order. */
async function savedSources(
  server: ReturnType<typeof serve>['server'],
  ids: (string | undefined)[],
) {
  await waitFor(
    () => {
      const sources = (server.versions[0]?.inputs.sources ?? []) as unknown as LineageSource[];
      expect(sources.map((source) => source.generation?.id)).toEqual(ids);
    },
    { timeout: 4_000 },
  );
}

/** Waits until no dialog is open, its closing transition included. */
async function closed() {
  await waitFor(
    () => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    },
    { timeout: 2_000 },
  );
}

describe('the Sources section of a Version (#125)', () => {
  it.each(['Cover', 'Sample This Song', 'Reuse Prompt'])(
    '%s takes one source: a Song searched for, then one of its Generations, saved by autosave',
    async (action) => {
      const { server } = serve();
      const user = await openVersion();

      await user.selectOptions(screen.getByRole('combobox', { name: 'Action' }), action);
      await user.click(screen.getByRole('button', { name: `Choose the ${action} source` }));
      await pick(user, 'Song B', 'n8-8-v1-g1');

      const sources = within(section()).getByRole('list', { name: 'Audio sources' });
      expect(within(sources).getByTestId('source-title')).toHaveTextContent('Song B take 1');
      expect(within(sources).getByTestId('source-shortcode')).toHaveTextContent('n8-8-v1-g1');
      expect(
        screen.queryByRole('button', { name: `Choose the ${action} source` }),
      ).not.toBeInTheDocument();
      expect(await lastWrite(server.writes, 1)).toEqual({
        inputs: {
          sources: [
            {
              typeId: typeId(action),
              generation: server.otherGenerations[0]?.id,
              continueAtSeconds: null,
              secondaryIds: null,
            },
          ],
        },
      });
    },
  );

  it('a user type mapped to an action is offered with it and follows its rules; an unmapped one is not offered (#126)', async () => {
    const reimagining = relationshipType(120, 'Reimagining of', 'Reimagined as', {
      sunoAction: 'cover',
    });
    const blend = relationshipType(121, 'Blend of', 'Blended into', { sunoAction: 'mashup' });
    const answer = relationshipType(122, 'Answer to', 'Answered by');
    const { server } = serve();
    server.relationshipTypes = [...SYSTEM_TYPES, answer, blend, reimagining];
    const user = await openVersion();

    const action = screen.getByRole('combobox', { name: 'Action' });
    const options = within(action)
      .getAllByRole('option')
      .map((option) => option.textContent);
    expect(options).toEqual([
      'None',
      'Cover',
      'Extend',
      'Reuse Prompt',
      'Mashup',
      'Sample This Song',
      'Blend of (Mashup)',
      'Reimagining of (Cover)',
    ]);

    // Mapped to Mashup, it asks for a second source.
    await user.selectOptions(action, 'Blend of (Mashup)');
    await user.click(screen.getByRole('button', { name: 'Choose the Blend of source' }));
    await pick(user, 'Song B', 'n8-8-v1-g1');
    expect(screen.getByTestId('mashup-needs-second')).toHaveTextContent(
      'A Mashup needs a second source.',
    );
    await lastWrite(server.writes, 1);

    // Mapped to Cover, it holds one source and hides Inspiration, as Cover does.
    await user.selectOptions(action, 'Reimagining of (Cover)');
    expect(await lastWrite(server.writes, 2)).toEqual({
      inputs: {
        sources: [
          {
            typeId: reimagining.id,
            generation: server.otherGenerations[0]?.id,
            continueAtSeconds: null,
            secondaryIds: null,
          },
        ],
      },
    });
    expect(screen.queryByRole('heading', { name: 'Inspiration' })).not.toBeInTheDocument();
    expect(screen.queryByTestId('mashup-needs-second')).not.toBeInTheDocument();
  });

  it('a source is replaced and removed; the picker leaves out the Version’s own Generations and labels every state', async () => {
    const { server } = serve();
    // The Version's Song is searchable too, but its own Generations are not offered.
    server.generations = [testGeneration('1', 1)];
    const user = await openVersion();

    await user.selectOptions(screen.getByRole('combobox', { name: 'Action' }), 'Cover');
    await user.click(screen.getByRole('button', { name: 'Choose the Cover source' }));
    const dialog = await openedDialog('Choose the Cover source');
    await user.type(within(dialog).getByRole('textbox', { name: 'Song' }), 'n8-7');
    await user.click(await within(dialog).findByRole('option', { name: /n8-7/ }));
    expect(await within(dialog).findByTestId('picker-no-generations')).toHaveTextContent(
      'besides this Version’s own',
    );
    await user.clear(within(dialog).getByRole('textbox', { name: 'Song' }));
    await user.type(within(dialog).getByRole('textbox', { name: 'Song' }), 'Song B');
    await user.click(await within(dialog).findByRole('option', { name: /Song B/ }));
    const archived = await within(dialog).findByRole('row', { name: /n8-8-v1-g2/ });
    expect(archived).toHaveTextContent('Archived');
    expect(archived).toHaveTextContent('In Suno Trash');
    await user.click(within(dialog).getByRole('button', { name: 'Choose n8-8-v1-g2' }));
    await closed();
    expect(within(section()).getByTestId('source-availability')).toHaveTextContent('In Suno Trash');
    await savedSources(server, [server.otherGenerations[1]?.id]);

    await user.click(screen.getByRole('button', { name: 'Replace Song B take 2' }));
    await pick(user, 'Song C', 'n8-9-v1-g1');
    expect(within(section()).getByTestId('source-title')).toHaveTextContent('Song C take 1');
    expect(within(section()).queryByTestId('source-availability')).not.toBeInTheDocument();
    await savedSources(server, [server.otherGenerations[3]?.id]);

    await user.click(screen.getByRole('button', { name: 'Remove Song C take 1' }));
    expect(
      within(section()).queryByRole('list', { name: 'Audio sources' }),
    ).not.toBeInTheDocument();
    await savedSources(server, []);
  });

  it('Mashup asks for a second source; going back to one action drops it after a confirmation', async () => {
    const { server } = serve();
    const user = await openVersion();

    await user.selectOptions(screen.getByRole('combobox', { name: 'Action' }), 'Mashup');
    await user.click(screen.getByRole('button', { name: 'Choose the Mashup source' }));
    await pick(user, 'Song B', 'n8-8-v1-g1');
    expect(screen.getByTestId('mashup-needs-second')).toHaveTextContent(
      'A Mashup needs a second source.',
    );
    await user.click(screen.getByRole('button', { name: 'Add the second source' }));
    // The first source cannot be added twice.
    const dialog = await openedDialog('Choose the Mashup source');
    await user.type(within(dialog).getByRole('textbox', { name: 'Song' }), 'Song B');
    await user.click(await within(dialog).findByRole('option', { name: /Song B/ }));
    expect(
      await within(dialog).findByRole('row', { name: /n8-8-v1-g1.*Already a source/ }),
    ).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Choose n8-8-v1-g3' }));
    await closed();

    const sources = within(section()).getByRole('list', { name: 'Audio sources' });
    expect(within(sources).getAllByRole('listitem')).toHaveLength(2);
    expect(screen.queryByRole('button', { name: 'Add the second source' })).not.toBeInTheDocument();
    expect(screen.queryByTestId('mashup-needs-second')).not.toBeInTheDocument();
    await savedSources(server, [server.otherGenerations[0]?.id, server.otherGenerations[2]?.id]);
    const writes = server.writes.length;

    // Sample This Song holds one: the second source goes, once confirmed.
    await user.selectOptions(screen.getByRole('combobox', { name: 'Action' }), 'Sample This Song');
    const confirm = await openedDialog('Change the sources?');
    expect(within(confirm).getByTestId('sources-confirmation')).toHaveTextContent(
      'The audio action becomes Sample This Song. This also removes the second source.',
    );
    await user.click(within(confirm).getByRole('button', { name: 'Change' }));
    await closed();
    expect(within(sources).getAllByRole('listitem')).toHaveLength(1);
    const after = await lastWrite(server.writes, writes + 1);
    expect(after).toEqual({
      inputs: {
        sources: [
          {
            typeId: typeId('Sample This Song'),
            generation: server.otherGenerations[0]?.id,
            continueAtSeconds: null,
            secondaryIds: null,
          },
        ],
      },
    });
  });

  it('Extend asks where to continue from, in minutes and seconds within the source’s length', async () => {
    const { server } = serve();
    const user = await openVersion();

    await user.selectOptions(screen.getByRole('combobox', { name: 'Action' }), 'Extend');
    await user.click(screen.getByRole('button', { name: 'Choose the Extend source' }));
    await pick(user, 'Song B', 'n8-8-v1-g1');
    expect(screen.getByText('Within the source’s 2:05.')).toBeInTheDocument();

    // 2:10 is past the end of a 2:05 source: refused, nothing saved.
    await user.type(screen.getByRole('textbox', { name: 'Minutes' }), '2');
    await user.type(screen.getByRole('textbox', { name: 'Seconds' }), '10');
    expect(await screen.findByTestId('continue-at-error')).toHaveTextContent(
      'The source is 2:05 long',
    );
    await user.clear(screen.getByRole('textbox', { name: 'Seconds' }));
    await user.type(screen.getByRole('textbox', { name: 'Seconds' }), '4.5');
    await waitFor(() => {
      expect(screen.queryByTestId('continue-at-error')).not.toBeInTheDocument();
    });
    await waitFor(
      () => {
        expect(server.versions[0]?.inputs.sources).toMatchObject([{ continueAtSeconds: 124.5 }]);
      },
      { timeout: 4_000 },
    );
  });

  it('Extend from a source of unknown length takes any position from zero', async () => {
    const { server } = serve();
    const user = await openVersion();

    await user.selectOptions(screen.getByRole('combobox', { name: 'Action' }), 'Extend');
    await user.click(screen.getByRole('button', { name: 'Choose the Extend source' }));
    await pick(user, 'Song B', 'n8-8-v1-g2');
    expect(screen.getByText(/length is not known/)).toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: 'Minutes' }), '75');
    await waitFor(
      () => {
        expect(server.versions[0]?.inputs.sources).toMatchObject([{ continueAtSeconds: 4500 }]);
      },
      { timeout: 4_000 },
    );
  });

  it('Inspiration takes up to four songs in an order the user changes; a fifth cannot be added', async () => {
    const { server } = serve();
    const user = await openVersion();
    const picks: [string, string][] = [
      ['Song B', 'n8-8-v1-g1'],
      ['Song B', 'n8-8-v1-g2'],
      ['Song B', 'n8-8-v1-g3'],
      ['Song C', 'n8-9-v1-g1'],
    ];
    for (const [song, shortcode] of picks) {
      await user.click(screen.getByRole('button', { name: 'Add an Inspiration song' }));
      await pick(user, song, shortcode);
    }

    const list = within(section()).getByRole('list', { name: 'Inspiration songs' });
    expect(
      within(list)
        .getAllByTestId('source-title')
        .map((title) => title.textContent),
    ).toEqual(['Song B take 1', 'Song B take 2', 'Song B take 3', 'Song C take 1']);
    // Complement: a fifth cannot be added.
    expect(
      screen.queryByRole('button', { name: 'Add an Inspiration song' }),
    ).not.toBeInTheDocument();
    expect(screen.getByTestId('inspiration-full')).toHaveTextContent(
      'Inspiration holds up to 4 songs.',
    );
    // While songs are chosen, no playlist is offered.
    expect(
      screen.queryByRole('combobox', { name: 'Or use a Suno playlist' }),
    ).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Move Song C take 1 up' }));
    await user.click(screen.getByRole('button', { name: 'Move Song B take 1 down' }));
    expect(
      within(list)
        .getAllByTestId('source-title')
        .map((title) => title.textContent),
    ).toEqual(['Song B take 2', 'Song B take 1', 'Song C take 1', 'Song B take 3']);
    expect(screen.getByRole('button', { name: 'Move Song B take 2 up' })).toBeDisabled();
    await waitFor(
      () => {
        expect(server.versions[0]?.inputs.inspiration).toMatchObject({
          sources: [
            { generation: { shortcode: 'n8-8-v1-g2' } },
            { generation: { shortcode: 'n8-8-v1-g1' } },
            { generation: { shortcode: 'n8-9-v1-g1' } },
            { generation: { shortcode: 'n8-8-v1-g3' } },
          ],
        });
      },
      { timeout: 6_000 },
    );
  });

  it('Inspiration may instead be one Suno playlist n8Tracks has seen, which then excludes songs', async () => {
    const { server } = serve();
    server.playlists = [
      {
        id: 'pl-1',
        name: 'Road trip',
        memberCount: 2,
        clipIds: ['c1', 'c2'],
        lastSeen: '2026-10-01T10:00:00Z',
      },
    ];
    const user = await openVersion();

    await user.selectOptions(
      await screen.findByRole('combobox', { name: 'Or use a Suno playlist' }),
      'Road trip (2)',
    );
    expect(screen.getByTestId('inspiration-playlist')).toHaveTextContent('Playlist: Road trip');
    expect(
      screen.queryByRole('button', { name: 'Add an Inspiration song' }),
    ).not.toBeInTheDocument();
    expect(await lastWrite(server.writes, 1)).toEqual({
      inputs: {
        inspiration: {
          playlist: { sunoPlaylistId: 'pl-1', name: 'Road trip', clipIds: ['c1', 'c2'] },
        },
      },
    });

    await user.click(screen.getByRole('button', { name: 'Remove the playlist' }));
    expect(screen.getByRole('button', { name: 'Add an Inspiration song' })).toBeEnabled();
  });

  it('with no import yet, the playlist and voice lists say so', async () => {
    serve();
    await openVersion();
    expect(await screen.findByTestId('no-playlists')).toHaveTextContent('none yet');
    expect(await screen.findByTestId('no-personas')).toHaveTextContent('No Suno voices yet');
  });

  it('choosing Cover removes Inspiration after a confirmation, and hides it while Cover is chosen', async () => {
    const { server } = serve();
    const user = await openVersion();
    await user.click(screen.getByRole('button', { name: 'Add an Inspiration song' }));
    await pick(user, 'Song C', 'n8-9-v1-g1');
    await lastWrite(server.writes, 1);

    await user.selectOptions(screen.getByRole('combobox', { name: 'Action' }), 'Cover');
    const confirm = await openedDialog('Change the sources?');
    expect(within(confirm).getByTestId('sources-confirmation')).toHaveTextContent(
      'This also removes Inspiration',
    );
    // Keeping it as it is changes nothing.
    await user.click(within(confirm).getByRole('button', { name: 'Keep as it is' }));
    expect(screen.getByRole('combobox', { name: 'Action' })).toHaveValue('');
    expect(screen.getByRole('heading', { name: 'Inspiration' })).toBeInTheDocument();

    await user.selectOptions(screen.getByRole('combobox', { name: 'Action' }), 'Cover');
    await user.click(
      within(await openedDialog('Change the sources?')).getByRole('button', { name: 'Change' }),
    );
    expect(screen.queryByRole('heading', { name: 'Inspiration' })).not.toBeInTheDocument();
    expect(await lastWrite(server.writes, 2)).toEqual({ inputs: { inspiration: null } });
  });

  it('a Voice is chosen from the voices seen in imported clips', async () => {
    const { server } = serve();
    server.personas = [
      { id: 'pe-1', name: 'Airy' },
      { id: 'pe-2', name: 'Smoky' },
    ];
    const user = await openVersion();
    await waitFor(() => {
      expect(screen.getByRole('combobox', { name: 'Voice' })).toBeEnabled();
    });
    await user.selectOptions(screen.getByRole('combobox', { name: 'Voice' }), 'Smoky');
    expect(await lastWrite(server.writes, 1)).toEqual({
      inputs: { voice: { personaId: 'pe-2', name: 'Smoky' } },
    });
  });

  it('an audio file note replaces the audio action once confirmed, and says it is attached by hand in Suno', async () => {
    const { server } = serve();
    const user = await openVersion();
    await user.selectOptions(screen.getByRole('combobox', { name: 'Action' }), 'Cover');
    await user.click(screen.getByRole('button', { name: 'Choose the Cover source' }));
    await pick(user, 'Song B', 'n8-8-v1-g1');
    await lastWrite(server.writes, 1);

    expect(within(section()).getByText(/attach it by hand in Suno/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Add an audio file note' }));
    const dialog = await openedDialog('Audio file note');
    expect(within(dialog).getByTestId('audio-note-replaces')).toHaveTextContent(
      'replaces this Version’s audio action (Cover)',
    );
    // A note needs a description.
    await user.click(within(dialog).getByRole('button', { name: 'Replace the audio action' }));
    expect(within(dialog).getByText('Describe the file.')).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: 'Description' }), 'Demo hum');
    await user.click(within(dialog).getByRole('button', { name: 'Replace the audio action' }));

    expect(within(section()).getByTestId('file-note-description')).toHaveTextContent('Demo hum');
    expect(screen.getByRole('combobox', { name: 'Action' })).toHaveValue('');
    expect(await lastWrite(server.writes, 2)).toEqual({
      inputs: { sources: [], fileInputs: [{ kind: 'audio', description: 'Demo hum' }] },
    });

    // Removing the note leaves no action.
    await user.click(screen.getByRole('button', { name: 'Remove the audio file note' }));
    expect(screen.getByRole('combobox', { name: 'Action' })).toHaveValue('');
  });

  it('complement: image and video notes are offered in Simple mode only, and kept but hidden in Advanced', async () => {
    serve({
      songMode: 'simple',
      fileInputs: [{ kind: 'image', description: 'Album art' }],
    });
    const user = await openVersion();
    expect(within(section()).getByTestId('file-note-description')).toHaveTextContent('Album art');
    expect(screen.getByRole('button', { name: 'Add a video note' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add an image note' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: 'Advanced' }));
    await waitFor(() => {
      expect(within(section()).queryByTestId('file-note-description')).not.toBeInTheDocument();
    });
    expect(screen.queryByRole('button', { name: 'Add a video note' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add an audio file note' })).toBeInTheDocument();
  });

  it('a pasted Suno address n8Tracks has not imported shows as a Suno clip, Not imported', async () => {
    const { server } = serve();
    const user = await openVersion();
    await user.selectOptions(screen.getByRole('combobox', { name: 'Action' }), 'Cover');
    await user.click(screen.getByRole('button', { name: 'Choose the Cover source' }));
    const dialog = await openedDialog('Choose the Cover source');
    const pasted = within(dialog).getByRole('textbox', { name: 'Suno song address or clip ID' });
    await user.type(pasted, 'not an address');
    await user.click(within(dialog).getByRole('button', { name: 'Use this clip' }));
    expect(within(dialog).getByText(/Paste a Suno song address/)).toBeInTheDocument();
    await user.clear(pasted);
    await user.type(pasted, 'https://suno.com/song/abcdef12-3456-7890-abcd-ef1234567890');
    await user.click(within(dialog).getByRole('button', { name: 'Use this clip' }));

    expect(within(section()).getByTestId('source-title')).toHaveTextContent('Suno clip abcdef12');
    expect(within(section()).getByTestId('source-availability')).toHaveTextContent('Not imported');
    expect(await lastWrite(server.writes, 1)).toMatchObject({
      inputs: { sources: [{ external: { sunoId: 'abcdef12-3456-7890-abcd-ef1234567890' } }] },
    });
  });

  it('a pasted ID n8Tracks already has becomes that Generation once saved', async () => {
    const { server } = serve();
    const { user, clock } = await openVersionOnClock();
    await user.selectOptions(screen.getByRole('combobox', { name: 'Action' }), 'Cover');
    await user.click(screen.getByRole('button', { name: 'Choose the Cover source' }));
    const dialog = await openedDialog('Choose the Cover source');
    await user.type(
      within(dialog).getByRole('textbox', { name: 'Suno song address or clip ID' }),
      'suno-n8-9-1',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Use this clip' }));
    await lastWrite(server.writes, 1);
    await waitFor(() => {
      expect(within(section()).getByTestId('source-shortcode')).toHaveTextContent('n8-9-v1-g1');
    });
    expect(within(section()).queryByTestId('source-availability')).not.toBeInTheDocument();
    // Taken as the API stored it, it is not sent again.
    await act(async () => {
      clock.advanceTimers(3_000);
      await Promise.resolve();
    });
    expect(server.writes).toHaveLength(1);
  });

  it('labels each source that cannot be used as it is: Not imported, Deleted, In Suno Trash, Remote Missing', async () => {
    const generation = otherGeneration(SONG_B, 1);
    serve({
      sources: [
        readSource(generation, {
          typeId: typeId('Mashup'),
          sunoAction: 'mashup',
          availability: 'trashed',
        }),
        {
          typeId: typeId('Mashup'),
          sunoAction: 'mashup',
          external: { sunoId: 'gone-clip', title: 'Old take', address: null, label: 'Deleted' },
          availability: 'deleted',
        },
      ],
      inspiration: {
        sources: [
          {
            external: { sunoId: 'fedcba98-0000', title: null, address: null, label: null },
            availability: 'not_imported',
          },
          readSource(otherGeneration(SONG_B, 3), { availability: 'missing' }),
        ],
      },
    });
    await openVersion();
    const labels = within(section())
      .getAllByTestId('source-availability')
      .map((label) => label.textContent);
    expect(labels).toEqual(['In Suno Trash', 'Deleted', 'Not imported', 'Remote Missing']);
    expect(within(section()).getByText('Suno clip fedcba98')).toBeInTheDocument();
  });

  it('complement: Inspiration and Voice are labelled Pro and nothing is disabled on that account', async () => {
    const { server } = serve();
    server.personas = [{ id: 'pe-1', name: 'Airy' }];
    await openVersion();
    expect(within(section()).getAllByTestId('pro-label')).toHaveLength(2);
    expect(screen.getByRole('button', { name: 'Add an Inspiration song' })).toBeEnabled();
    await waitFor(() => {
      expect(screen.getByRole('combobox', { name: 'Voice' })).toBeEnabled();
    });
  });

  it('on a frozen Version the section is read-only', async () => {
    const generation = otherGeneration(SONG_B, 1);
    serve(
      {
        sources: [readSource(generation, { typeId: typeId('Cover'), sunoAction: 'cover' })],
        voice: { personaId: 'pe-1', name: 'Airy' },
        fileInputs: [{ kind: 'audio', description: 'ignored' }],
      },
      { isFrozen: true },
    );
    fakeTimeouts();
    renderApp('/songs/n8-7');
    await screen.findByRole('heading', { name: 'Sources' });

    expect(screen.getByTestId('sources-frozen')).toHaveTextContent('frozen with this Version');
    expect(screen.getByTestId('audio-action')).toHaveTextContent('Cover');
    expect(within(section()).getByTestId('source-shortcode')).toHaveTextContent('n8-8-v1-g1');
    expect(screen.getByTestId('voice')).toHaveTextContent('Airy');
    expect(within(section()).queryByRole('button')).not.toBeInTheDocument();
    expect(within(section()).queryByRole('combobox')).not.toBeInTheDocument();
  });

  it('links a source to its Generation, and offers a Not imported one’s Suno address and the ignore list, frozen or not', async () => {
    Object.defineProperty(window.navigator, 'clipboard', {
      value: { writeText: () => Promise.resolve() },
      configurable: true,
    });
    const generation = otherGeneration(SONG_B, 1);
    const { server } = serve(
      {
        sources: [
          readSource(generation, { typeId: typeId('Mashup'), sunoAction: 'mashup' }),
          {
            typeId: typeId('Mashup'),
            sunoAction: 'mashup',
            external: {
              sunoId: 'source-x',
              title: 'Never imported',
              address: 'https://suno.com/song/source-x',
              label: 'Not imported',
            },
            availability: 'not_imported',
          },
          {
            typeId: typeId('Mashup'),
            sunoAction: 'mashup',
            external: { sunoId: 'gone-clip', title: 'Old take', address: null, label: 'Deleted' },
            availability: 'deleted',
          },
        ],
      },
      { isFrozen: true },
    );
    const user = await openFrozen();

    expect(within(section()).getByRole('link', { name: 'Song B take 1' })).toHaveAttribute(
      'href',
      `/songs/${SONG_B.shortcode}/generations/${generation.shortcode}`,
    );
    expect(within(section()).getByText('Never imported')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Copy Suno address of Never imported' }));
    expect(await within(section()).findByText('Suno address copied.')).toBeInTheDocument();

    await user.click(
      screen.getByRole('button', { name: 'Add to the ignore list: Never imported' }),
    );
    expect(
      await within(section()).findByText(
        'Added to the ignore list. It stays a source of this Version.',
      ),
    ).toBeInTheDocument();
    expect(server.ignoredSources).toEqual(['source-x']);
    // The source stays as it was.
    expect(
      within(section())
        .getAllByTestId('source-availability')
        .map((label) => label.textContent),
    ).toEqual(['Not imported', 'Deleted']);

    // Complement: a source that is not Not imported offers neither.
    expect(
      screen.queryByRole('button', { name: /Old take|Song B take 1/ }),
    ).not.toBeInTheDocument();
    Reflect.deleteProperty(window.navigator, 'clipboard');
  });

  it('Create New Version From carries the sources into the new Version, where an unavailable one is replaced', async () => {
    serve(
      {
        sources: [
          {
            typeId: typeId('Cover'),
            sunoAction: 'cover',
            external: { sunoId: 'gone-clip', title: 'Old take', address: null, label: 'Deleted' },
            availability: 'deleted',
          },
        ],
      },
      { isFrozen: true },
    );
    const user = await openFrozen();
    expect(within(section()).queryByRole('button')).not.toBeInTheDocument();

    // The header's button (the frozen notice offers the same).
    const create = screen.getAllByRole('button', { name: 'Create New Version From 1' }).at(0);
    expect(create).toBeDefined();
    await user.click(create ?? document.body);
    const dialog = await openedDialog('Create New Version From 1');
    await user.click(within(dialog).getByRole('button', { name: 'Create Version' }));
    await closed();

    // The new Version is mutable and holds the same source, still labelled.
    await waitFor(() => {
      expect(screen.getByRole('heading', { name: /^Version (?!1$)/ })).toBeInTheDocument();
    });
    await waitFor(() => {
      expect(screen.queryByTestId('sources-frozen')).not.toBeInTheDocument();
    });
    expect(within(section()).getByTestId('source-title')).toHaveTextContent('Old take');
    expect(within(section()).getByTestId('source-availability')).toHaveTextContent('Deleted');
    await user.click(screen.getByRole('button', { name: 'Replace Old take' }));
    await pick(user, 'Song B', 'n8-8-v1-g1');
    expect(within(section()).getByTestId('source-title')).toHaveTextContent('Song B take 1');
    expect(within(section()).queryByTestId('source-availability')).not.toBeInTheDocument();
  });

  it('is shown for a Song Version only', async () => {
    serve({ kind: 'speech' });
    fakeTimeouts();
    renderApp('/songs/n8-7');
    await screen.findByRole('radiogroup', { name: 'Kind' });
    expect(screen.queryByRole('heading', { name: 'Sources' })).not.toBeInTheDocument();
  });
});
