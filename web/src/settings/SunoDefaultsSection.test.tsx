import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { OptionValue } from '../api/createFields';
import type { SunoModel } from '../api/sunoModels';
import type { VersionDefaults } from '../api/versionDefaults';
import { CREATE_FIELDS } from '../test/createFieldsFixture';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

const KEYS = [
  'kind',
  'songMode',
  'speechMode',
  'model',
  'vocalGender',
  'durationMode',
  'durationSeconds',
  'maxMode',
  'weirdness',
  'styleInfluence',
  'variety',
  'personalize',
  'speechVocalGender',
  'speechBackgroundMusic',
  'speechVariety',
  'soundsModel',
  'soundType',
  'soundBpm',
  'soundKey',
  'soundScale',
];

const model = (name: string, order: number, retired = false): SunoModel => ({
  id: `01a10a6e-dd0${String(order)}-7000-8000-00000000000${String(order)}`,
  name,
  note: null,
  order,
  retired,
  discovered: false,
  versionCount: 0,
});

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Response {
  return new Response(JSON.stringify({ status, code, title: 'refused', ...extra }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

/**
 * A fake n8Tracks holding the defaults and the model list, answering as the API does: a PUT is
 * checked against the defaults' revision (409 `revision_conflict` with `current` when stale) and
 * answered with the defaults at the next revision; a default naming a retired model is `ignored`.
 * Retiring a model raises the list revision. `server.next` answers the next PUT some other way.
 */
function defaultsServer(defaults: Record<string, OptionValue> = {}) {
  const server = {
    revision: 1,
    defaults,
    models: [model('v6', 1), model('v6-wild', 2), model('v6-mini', 3)],
    modelsRevision: 1,
    puts: [] as { ifMatch: string | null; body: unknown }[],
    next: undefined as (() => Response) | undefined,
    view(): VersionDefaults {
      const offered = server.models.filter((item) => !item.retired).map((item) => item.name);
      const ignored = Object.fromEntries(
        ['model', 'soundsModel'].flatMap((key) => {
          const value = server.defaults[key];
          return typeof value === 'string' && !offered.includes(value)
            ? [[key, `${value} is retired or no longer on the model list.`]]
            : [];
        }),
      );
      return {
        revision: server.revision,
        defaults: server.defaults,
        ignored,
        keys: KEYS,
        models: offered,
      };
    },
  };

  const mock = stubFetch();
  mock.mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/suno/create-fields')) {
      return Promise.resolve(
        jsonResponse(200, {
          ...CREATE_FIELDS,
          models: server.models.filter((item) => !item.retired).map((item) => item.name),
        }),
      );
    }
    if (path.includes('/api/v1/suno/models')) {
      if (method === 'PATCH') {
        const body = JSON.parse(typeof init?.body === 'string' ? init.body : 'null') as {
          retired: boolean;
        };
        const id = /\/suno\/models\/([^/?]+)/.exec(path)?.[1];
        server.models = server.models.map((item) =>
          item.id === id ? { ...item, retired: body.retired } : item,
        );
        server.modelsRevision += 1;
      }
      return Promise.resolve(
        jsonResponse(200, { revision: server.modelsRevision, items: server.models }),
      );
    }
    if (!path.endsWith('/api/v1/settings/version-defaults')) {
      return Promise.resolve(problem(404, 'not_found'));
    }
    if (method === 'GET') {
      return Promise.resolve(jsonResponse(200, server.view()));
    }

    const headers = new Headers(init?.headers);
    const body = JSON.parse(typeof init?.body === 'string' ? init.body : 'null') as {
      defaults: Record<string, OptionValue>;
    };
    server.puts.push({ ifMatch: headers.get('If-Match'), body });
    const next = server.next;
    if (next) {
      server.next = undefined;
      return Promise.resolve(next());
    }
    if (headers.get('If-Match') !== `"${String(server.revision)}"`) {
      return Promise.resolve(problem(409, 'revision_conflict', { current: server.view() }));
    }
    server.revision += 1;
    server.defaults = body.defaults;
    return Promise.resolve(jsonResponse(200, server.view()));
  });

  return server;
}

const section = async () =>
  within(await screen.findByRole('region', { name: 'Defaults for new Songs' }));

const part = async (name: string) => within(await (await section()).findByRole('region', { name }));

const saveButton = async () => (await section()).getByRole('button', { name: 'Save defaults' });

describe('SunoDefaultsSection', () => {
  it("shows Suno's defaults for every kind, and no text option", async () => {
    defaultsServer();
    renderApp('/settings/suno');

    const song = await part('Song');
    expect(await song.findByLabelText('Variety')).toHaveValue('normal');
    expect(song.getByRole('combobox', { name: 'Model version' })).toHaveValue('v6');
    expect(song.getByRole('slider', { name: 'Weirdness' })).toHaveAttribute('aria-valuenow', '50');
    expect((await part('Speech')).getByRole('switch', { name: 'Background music' })).toBeChecked();
    expect((await part('Sound')).getByLabelText('Key')).toHaveValue('any');

    const all = await section();
    for (const text of ['Song description', 'Song Title', 'Exclude styles', 'Script', 'Sound']) {
      expect(all.queryByRole('textbox', { name: text })).not.toBeInTheDocument();
    }
    expect(all.queryByText('Your default')).not.toBeInTheDocument();
    expect(all.getAllByText("Suno's default").length).toBeGreaterThan(10);
    expect(await saveButton()).toBeDisabled();
  });

  it('shows a conditional option only when the other defaults meet its condition', async () => {
    const user = userEvent.setup();
    defaultsServer();
    renderApp('/settings/suno');

    const song = await part('Song');
    await song.findByLabelText('Variety');
    expect(song.queryByRole('slider', { name: 'Duration (custom)' })).not.toBeInTheDocument();
    await user.selectOptions(song.getByLabelText('Duration'), 'custom');
    expect(await song.findByRole('slider', { name: 'Duration (custom)' })).toBeInTheDocument();

    const sound = await part('Sound');
    expect(sound.queryByLabelText('Key scale')).not.toBeInTheDocument();
    await user.selectOptions(sound.getByLabelText('Key'), 'D#');
    expect(await sound.findByLabelText('Key scale')).toBeInTheDocument();
  });

  it('saves the defaults the user set, and only those, with the revision read', async () => {
    const user = userEvent.setup();
    const server = defaultsServer();
    renderApp('/settings/suno');

    const song = await part('Song');
    await user.selectOptions(await song.findByLabelText('Variety'), 'high');
    await user.selectOptions(song.getByRole('combobox', { name: 'Model version' }), 'v6-wild');
    const weirdness = song.getByRole('slider', { name: 'Weirdness' });
    weirdness.focus();
    for (let step = 0; step < 20; step += 1) {
      await user.keyboard('{ArrowLeft}');
    }
    await user.click((await part('Speech')).getByRole('switch', { name: 'Background music' }));
    expect(song.getAllByText('Your default')).toHaveLength(3);

    await user.click(await saveButton());

    expect(await screen.findByText('Defaults saved. New Songs start with them.')).toBeVisible();
    expect(server.puts).toEqual([
      {
        ifMatch: '"1"',
        body: {
          defaults: {
            variety: 'high',
            model: 'v6-wild',
            weirdness: 30,
            speechBackgroundMusic: false,
          },
        },
      },
    ]);
    expect(await saveButton()).toBeDisabled();
  });

  it("puts an option back to Suno's default", async () => {
    const user = userEvent.setup();
    const server = defaultsServer({ weirdness: 30, variety: 'high' });
    renderApp('/settings/suno');

    const song = await part('Song');
    expect(await song.findByLabelText('Variety')).toHaveValue('high');
    await user.click(song.getByRole('button', { name: "Use Suno's default for Variety" }));
    expect(song.getByLabelText('Variety')).toHaveValue('normal');
    await user.click(await saveButton());

    await waitFor(() => {
      expect(server.puts.at(-1)?.body).toEqual({ defaults: { weirdness: 30 } });
    });
  });

  it('flags a default whose model is retired, also when it is retired on the same page', async () => {
    const user = userEvent.setup();
    defaultsServer({ model: 'v6-wild' });
    renderApp('/settings/suno');

    const song = await part('Song');
    expect(await song.findByRole('combobox', { name: 'Model version' })).toHaveValue('v6-wild');
    expect(screen.queryByTestId('ignored-model')).not.toBeInTheDocument();

    await user.click(await screen.findByRole('button', { name: 'Retire v6-wild' }));

    expect(await screen.findByTestId('ignored-model')).toHaveTextContent(
      'v6-wild is retired or no longer on the model list.',
    );
    // The default is kept: the picker still shows it, marked retired.
    expect(song.getByRole('option', { name: 'v6-wild (retired)' })).toBeInTheDocument();
  });

  it('reloads the defaults when they were changed elsewhere', async () => {
    const user = userEvent.setup();
    const server = defaultsServer();
    renderApp('/settings/suno');

    const song = await part('Song');
    await user.selectOptions(await song.findByLabelText('Variety'), 'max');
    server.revision = 2;
    server.defaults = { variety: 'off' };
    await user.click(await saveButton());

    expect(await screen.findByText(/changed somewhere else/)).toBeVisible();
    expect(song.getByLabelText('Variety')).toHaveValue('off');
  });

  it('shows a refused default beside its control', async () => {
    const user = userEvent.setup();
    const server = defaultsServer();
    renderApp('/settings/suno');

    const song = await part('Song');
    await user.selectOptions(await song.findByLabelText('Variety'), 'max');
    server.next = () =>
      problem(422, 'validation_failed', {
        errors: { 'defaults.variety': ['Choose Variety: off, normal.'] },
      });
    await user.click(await saveButton());

    expect(await song.findByText('Choose Variety: off, normal.')).toBeVisible();
    expect(screen.getByText('Some defaults were not accepted: see each one.')).toBeVisible();
  });
});
