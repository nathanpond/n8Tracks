import { MantineProvider } from '@mantine/core';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import type { GenerationRequest } from '../api/generationRequests';
import { REQUEST_POLL_MS } from '../api/generationRequests';
import type { Bridge, BridgeMessage, BridgeReply, ExtensionState } from '../extension/bridge';
import { stateOfPong } from '../extension/bridge';
import { BridgeContext } from '../extension/bridgeContext';
import {
  advanceTimers,
  fakeTimeouts,
  healthyReport,
  jsonResponse,
  requestPath,
  stubFetch,
} from '../test/helpers';
import { GenerateOnSunoButton, GenerateOnSunoStatus } from './GenerateOnSuno';
import { useGenerateOnSuno } from './useGenerateOnSuno';

const VERSION_ID = '0199b1a0-7000-7000-9000-000000000001';
const REQUEST_ID = '0199b1a0-7000-7000-9000-0000000000aa';

function testRequest(change: Partial<GenerationRequest> = {}): GenerationRequest {
  return {
    id: REQUEST_ID,
    versionId: VERSION_ID,
    state: 'pending',
    active: true,
    step: null,
    message: null,
    claimed: false,
    createdAt: '2026-10-07T09:00:00Z',
    updatedAt: '2026-10-07T09:00:00Z',
    endedAt: null,
    ...change,
  };
}

/** A fake extension: answers detection with `state`, and `generate` with `reply`. */
function fakeBridge(state: ExtensionState, reply: BridgeReply = { type: 'generate-accepted' }) {
  const sent: BridgeMessage[] = [];
  const bridge: Bridge = {
    detect: () => Promise.resolve(state),
    send: (message) => {
      sent.push(message);
      return Promise.resolve(message.type === 'generate' ? reply : { type: 'options-opened' });
    },
  };
  return { bridge, sent };
}

/** A fake n8Tracks for one Version's requests: the current one is `current`, a POST makes `created`. */
function serve(options: {
  current?: GenerationRequest | null;
  create?: () => Response;
  cancel?: () => Response;
}) {
  const server = {
    current: options.current ?? null,
    creates: 0,
    cancels: [] as string[],
    reads: 0,
  };
  const mock = stubFetch();
  mock.mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (method === 'GET' && path.endsWith(`/versions/${VERSION_ID}/generation-request`)) {
      server.reads += 1;
      return Promise.resolve(jsonResponse(200, { request: server.current }));
    }
    if (method === 'POST' && path.endsWith(`/versions/${VERSION_ID}/generation-requests`)) {
      server.creates += 1;
      const response = options.create?.() ?? jsonResponse(201, testRequest());
      if (response.status === 201) {
        server.current = testRequest();
      }
      return Promise.resolve(response);
    }
    if (method === 'POST' && path.endsWith(`/generation-requests/${REQUEST_ID}/cancel`)) {
      server.cancels.push(REQUEST_ID);
      server.current = testRequest({
        state: 'cancelled',
        active: false,
        message: 'You cancelled it.',
        endedAt: '2026-10-07T09:01:00Z',
      });
      return Promise.resolve(options.cancel?.() ?? jsonResponse(200, server.current));
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return server;
}

function Harness() {
  const controller = useGenerateOnSuno(VERSION_ID);
  return (
    <>
      <GenerateOnSunoButton controller={controller} />
      <GenerateOnSunoStatus controller={controller} />
    </>
  );
}

function renderAction(bridge: Bridge) {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <BridgeContext.Provider value={bridge}>
          <Harness />
        </BridgeContext.Provider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

async function choose() {
  const user = userEvent.setup();
  const button = await screen.findByRole('button', { name: 'Generate on Suno' });
  await waitFor(() => {
    expect(button).toBeEnabled();
  });
  await user.click(button);
  return user;
}

describe('Generate on Suno', () => {
  it('makes a request and hands its ID to the extension when the extension is ready', async () => {
    const server = serve({});
    const { bridge, sent } = fakeBridge({ kind: 'ready', extensionVersion: '0.1.0' });
    renderAction(bridge);

    await choose();

    const state = await screen.findByTestId('generation-request-state');
    expect(state).toHaveTextContent('Generate on Suno: Handed to the extension');
    expect(server.creates).toBe(1);
    expect(sent).toEqual([{ type: 'generate', requestId: REQUEST_ID }]);
    expect(screen.getByRole('button', { name: 'Generate on Suno' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Cancel the request' })).toBeEnabled();
    expect(screen.getByText(/Nothing has been generated/)).toBeInTheDocument();
  });

  it.each<[string, ExtensionState, RegExp, boolean]>([
    ['absent', { kind: 'absent' }, /did not answer/, false],
    ['disconnected', { kind: 'disconnected', status: 'not-paired' }, /is not connected/, true],
    [
      'incompatible',
      { kind: 'incompatible', extensionVersion: '0.1.0', applicationVersion: '0.2.0' },
      /needs updating/,
      true,
    ],
    [
      'without suno.generate',
      { kind: 'no-scope', credentialName: 'Laptop' },
      /cannot generate/,
      true,
    ],
  ])(
    'says what to do and makes nothing when the extension is %s',
    async (_name, state, title, canOpen) => {
      const server = serve({});
      const { bridge, sent } = fakeBridge(state);
      renderAction(bridge);

      const user = await choose();

      const problem = await screen.findByTestId('extension-problem');
      expect(within(problem).getByRole('alert')).toHaveTextContent(title);
      expect(problem).toHaveTextContent('no request was made');
      expect(server.creates).toBe(0);
      expect(sent).toEqual([]);
      const name = 'Open the extension’s options';
      if (canOpen) {
        await user.click(within(problem).getByRole('button', { name }));
        expect(sent).toEqual([{ type: 'open-options' }]);
      } else {
        expect(within(problem).queryByRole('button', { name })).toBeNull();
      }
    },
  );

  it('names each blocking source and when Suno was last synced, and makes nothing', async () => {
    serve({
      create: () =>
        jsonResponse(422, {
          code: 'sources_unavailable',
          sources: [
            {
              group: 'audio',
              position: 1,
              title: 'Night drive',
              shortcode: 'n8-3-v1-g2',
              availability: 'trashed',
            },
            {
              group: 'inspiration',
              position: 2,
              title: null,
              shortcode: 'n8-4',
              availability: 'deleted',
            },
          ],
          lastSyncAt: '2026-10-06T12:00:00Z',
        }),
    });
    const { bridge, sent } = fakeBridge({ kind: 'ready', extensionVersion: '0.1.0' });
    renderAction(bridge);

    await choose();

    const blocked = await screen.findByTestId('sources-blocked');
    const items = within(blocked).getAllByTestId('blocked-source');
    expect(items.map((item) => item.textContent)).toEqual([
      'Audio source 1, Night drive (n8-3-v1-g2): in Suno’s Trash',
      'Inspiration source 2, n8-4: deleted from n8Tracks',
    ]);
    expect(within(blocked).getByTestId('last-sync')).toHaveTextContent(
      /as of the last confirmed sync, .*2026/,
    );
    expect(sent).toEqual([]);
    expect(screen.queryByTestId('generation-request')).toBeNull();
  });

  it('cancels a request the extension did not take, and says why', async () => {
    const server = serve({});
    const { bridge } = fakeBridge(
      { kind: 'ready', extensionVersion: '0.1.0' },
      { type: 'error', error: 'refused', message: 'n8Tracks refused the claim: 409.' },
    );
    renderAction(bridge);

    await choose();

    expect(await screen.findByTestId('handoff-problem')).toHaveTextContent(
      'The extension did not take the request: n8Tracks refused the claim: 409.',
    );
    expect(server.cancels).toEqual([REQUEST_ID]);
    await waitFor(() => {
      expect(screen.getByTestId('generation-request-state')).toHaveTextContent('Cancelled');
    });
  });

  it('follows the request as the extension reports it, every two seconds while it is active', async () => {
    fakeTimeouts();
    const server = serve({ current: testRequest({ state: 'claimed', claimed: true }) });
    const { bridge } = fakeBridge({ kind: 'ready', extensionVersion: '0.1.0' });
    renderAction(bridge);

    const state = await screen.findByTestId('generation-request-state');
    expect(state).toHaveTextContent('The extension has the request');
    const steps: [Partial<GenerationRequest>, string][] = [
      [{ state: 'opening' }, 'Opening Suno'],
      [{ state: 'workspace' }, 'Choosing the Song’s workspace in Suno'],
      // The extension's panel asks the user to choose the Song's workspace (#145).
      [
        { state: 'workspace', step: 'choose workspace' },
        'Waiting for you in Suno: choose the Song’s workspace in the extension’s panel',
      ],
      [{ state: 'filling' }, 'Filling Suno’s Create form'],
      [{ state: 'waiting' }, 'Waiting for you to click Create in Suno'],
      [
        {
          state: 'stopped',
          active: false,
          step: 'fill-lyrics',
          message: 'The lyrics field was not found.',
        },
        'Stopped at step “fill-lyrics”',
      ],
    ];
    for (const [change, label] of steps) {
      server.current = testRequest({ claimed: true, ...change });
      advanceTimers(REQUEST_POLL_MS);
      await waitFor(() => {
        expect(screen.getByTestId('generation-request-state')).toHaveTextContent(label);
      });
    }
    expect(screen.getByTestId('generation-request-message')).toHaveTextContent(
      'The lyrics field was not found.',
    );

    // Once it has ended it is not read again, and a new one may be made.
    const reads = server.reads;
    advanceTimers(REQUEST_POLL_MS * 3);
    expect(server.reads).toBe(reads);
    expect(screen.getByRole('button', { name: 'Generate on Suno' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Cancel the request' })).toBeNull();
  });

  it('shows a finished request as done', async () => {
    serve({ current: testRequest({ state: 'done', active: false, claimed: true }) });
    renderAction(fakeBridge({ kind: 'ready', extensionVersion: '0.1.0' }).bridge);

    expect(await screen.findByTestId('generation-request-state')).toHaveTextContent(
      'Generate on Suno: Done',
    );
  });

  it('shows the extension’s verification summary of Suno’s form, text by its length only', async () => {
    const hash = (digit: string) => ({ length: 11, sha256: digit.repeat(64) });
    serve({
      current: testRequest({
        state: 'waiting',
        claimed: true,
        step: 'review and create',
        verification: {
          adapterVersion: 5,
          mode: 'advanced',
          checkedAt: '2026-10-07T09:02:00Z',
          entries: [
            { key: 'songs.advanced.lyrics', outcome: 'set', expected: hash('a') },
            { key: 'songs.advanced.weirdness', outcome: 'failed', expected: 70, found: 65 },
            {
              key: 'songs.advanced.styles',
              outcome: 'failed',
              expected: hash('a'),
              found: hash('b'),
            },
            { key: 'songs.advanced.variety', outcome: 'failed', expected: 4, found: 2 },
            {
              key: 'songs.advanced.model',
              outcome: 'unavailable',
              expected: 'v6-wild',
              note: 'Suno’s model menu does not offer this model.',
            },
            {
              key: 'songs.advanced.audio',
              outcome: 'manual',
              note: 'Attach the file by hand: the demo.',
            },
            { key: 'songs.advanced.inspiration', outcome: 'not_applicable' },
            { key: 'songs.advanced.crop', outcome: 'unsupported' },
          ],
        },
      }),
    });
    renderAction(fakeBridge({ kind: 'ready', extensionVersion: '0.1.0' }).bridge);

    const summary = await screen.findByTestId('verification');
    expect(within(summary).getByTestId('verification-review')).toHaveTextContent(
      'Some entries need you before you generate',
    );
    expect(within(summary).getByTestId('verification-counts')).toHaveTextContent(
      '1 set, 3 differs, 1 unavailable, 1 to do by hand, 1 not applicable, 1 unsupported',
    );
    expect(
      within(summary)
        .getAllByTestId('verification-entry')
        .map((entry) => entry.textContent),
    ).toEqual([
      'Lyrics: Set',
      'Weirdness: Differs — expected 70, found 65',
      'Styles: Differs — expected the Version’s text, found other text of the same length',
      'Variety: Differs — expected max, found high',
      'Model: Unavailable — Suno’s model menu does not offer this model.',
      'Audio: To do by hand — Attach the file by hand: the demo.',
      'Inspo: Not applicable',
      'songs.advanced.crop: Unsupported',
    ]);
    expect(screen.getByTestId('generation-request-state')).toHaveTextContent(
      'Waiting for you to click Create in Suno',
    );
  });

  it('says every entry is as the Version says when nothing needs the user', async () => {
    serve({
      current: testRequest({
        state: 'waiting',
        claimed: true,
        verification: {
          adapterVersion: 5,
          mode: 'simple',
          checkedAt: '2026-10-07T09:02:00Z',
          entries: [
            { key: 'songs.simple.model', outcome: 'set', expected: 'v6-mini' },
            // A source the extension loaded and saw on the form (#148).
            { key: 'songs.simple.audio', outcome: 'verified', note: 'On the form.' },
          ],
        },
      }),
    });
    renderAction(fakeBridge({ kind: 'ready', extensionVersion: '0.1.0' }).bridge);

    expect(await screen.findByTestId('verification-review')).toHaveTextContent(
      'Every entry is as the Version says',
    );
    expect(screen.getByTestId('verification-counts')).toHaveTextContent('1 set, 1 verified');
    expect(screen.getByText(/Verification of Suno’s form \(Simple\)/)).toBeInTheDocument();
  });

  it('says what each of the user’s Creates came to: the Version, the options that differed, and those assumed (#149)', async () => {
    serve({
      current: testRequest({
        state: 'waiting',
        claimed: true,
        step: 'Create recorded',
        message:
          '1 Generation recorded on the new Version n8-1-v1.1, made from what was submitted.',
        observed: [
          {
            observedAt: '2026-10-07T09:03:00Z',
            outcome: 'attached',
            version: { id: VERSION_ID, number: '1', shortcode: 'n8-1-v1' },
            differing: [],
            assumed: ['model'],
            requestRead: true,
            generations: [
              { id: 'g-1', shortcode: 'n8-1-v1-g1', sunoId: 'clip-1' },
              { id: 'g-2', shortcode: 'n8-1-v1-g2', sunoId: 'clip-2' },
            ],
            skipped: [],
          },
          {
            observedAt: '2026-10-07T09:05:00Z',
            outcome: 'branched',
            version: { id: 'v-2', number: '1.1', shortcode: 'n8-1-v1.1' },
            differing: ['weirdness', 'styleInfluence', 'sources'],
            assumed: ['model', 'vocalGender'],
            requestRead: false,
            generations: [{ id: 'g-3', shortcode: 'n8-1-v1.1-g1', sunoId: 'clip-3' }],
            skipped: [{ sunoId: 'clip-4', reason: 'tombstoned' }],
          },
        ],
      }),
    });
    renderAction(fakeBridge({ kind: 'ready', extensionVersion: '0.1.0' }).bridge);

    const creates = await screen.findAllByTestId('observed-create');
    expect(creates.map((create) => create.dataset.outcome)).toEqual(['attached', 'branched']);
    const [attached, branched] = creates;
    if (attached === undefined || branched === undefined) {
      throw new Error('Both Creates are shown.');
    }
    expect(attached).toHaveTextContent(
      '2 Generations recorded on this request’s Version 1, which is now frozen.',
    );
    expect(within(attached).queryByTestId('observed-differing')).toBeNull();
    expect(branched).toHaveTextContent(
      'You changed the form, so 1 generation went to a new Version 1.1, made from what was submitted; this Version is unchanged.',
    );
    expect(within(branched).getByTestId('observed-differing')).toHaveTextContent(
      'Options that differed: Weirdness, Style Influence, Sources.',
    );
    expect(within(branched).getByTestId('observed-assumed')).toHaveTextContent(
      'Taken from the Version, because Suno did not say: Model, Vocal Gender. What the page sent could not be read.',
    );
    expect(within(branched).getByRole('link', { name: 'Open Version 1.1' })).toHaveAttribute(
      'href',
      '/go/n8-1-v1.1',
    );
    expect(within(branched).getByTestId('observed-skipped')).toHaveTextContent(
      'Skipped clip clip-4: deleted from n8Tracks, so it stays deleted.',
    );
  });

  it('cancels an active request on request', async () => {
    const server = serve({ current: testRequest({ state: 'waiting', claimed: true }) });
    renderAction(fakeBridge({ kind: 'ready', extensionVersion: '0.1.0' }).bridge);
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Cancel the request' }));

    expect(server.cancels).toEqual([REQUEST_ID]);
    expect(await screen.findByTestId('generation-request-message')).toHaveTextContent(
      'You cancelled it.',
    );
    expect(screen.getByTestId('generation-request-state')).toHaveTextContent('Cancelled');
  });
});

describe('the extension state a pong says', () => {
  it.each<[string, BridgeReply, ExtensionState['kind']]>([
    ['no answer', null, 'absent'],
    ['another answer', { type: 'error' }, 'absent'],
    ['not paired', { type: 'pong', connection: { status: 'not-paired' } }, 'disconnected'],
    ['still checking', { type: 'pong', connection: { status: 'checking' } }, 'disconnected'],
    [
      'an incompatible version',
      {
        type: 'pong',
        extensionVersion: '0.1.0',
        connection: { status: 'connected', compatible: false, scopes: ['suno.generate'] },
      },
      'incompatible',
    ],
    [
      'a credential without suno.generate',
      {
        type: 'pong',
        extensionVersion: '0.1.0',
        connection: { status: 'connected', compatible: true, scopes: ['suno.sync'] },
      },
      'no-scope',
    ],
    [
      'ready',
      {
        type: 'pong',
        extensionVersion: '0.1.0',
        connection: { status: 'connected', compatible: true, scopes: ['suno.generate'] },
      },
      'ready',
    ],
  ])('%s', (_name, pong, kind) => {
    expect(stateOfPong(pong).kind).toBe(kind);
  });
});

describe('the window bridge', () => {
  it('takes only an answer from its own window and origin with the id it sent', async () => {
    const { windowBridge } = await import('../extension/bridge');
    const origin = window.location.origin;
    const answer = (data: Record<string, unknown>, change: Partial<MessageEventInit> = {}) => {
      window.dispatchEvent(
        new MessageEvent('message', { data, source: window, origin, ...change }),
      );
    };
    const posted: unknown[] = [];
    const post = vi.spyOn(window, 'postMessage').mockImplementation((message: unknown) => {
      const data = message as Record<string, unknown>;
      posted.push(data);
      const pong = {
        source: 'n8tracks-extension',
        id: data.id,
        type: 'pong',
        extensionVersion: '0.1.0',
        connection: { status: 'connected', compatible: true, scopes: ['suno.generate'] },
      };
      // Ignored: another id, another origin, another window, another source.
      answer({ ...pong, id: 'other' });
      answer(pong, { origin: 'https://elsewhere.example' });
      answer(pong, { source: null });
      answer({ ...pong, source: 'n8tracks' });
      answer({ ...pong, extensionVersion: '9.9.9' });
    });
    try {
      const state = await windowBridge(window).detect();
      expect(state).toEqual({ kind: 'ready', extensionVersion: '9.9.9' });
      expect(posted).toEqual([expect.objectContaining({ type: 'ping', source: 'n8tracks' })]);
      expect(post).toHaveBeenCalledWith(expect.anything(), origin);
    } finally {
      post.mockRestore();
    }
  });

  it('says the extension is absent when nothing answers within half a second', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const { windowBridge, PING_TIMEOUT_MS } = await import('../extension/bridge');
    const detected = windowBridge(window).detect();
    vi.advanceTimersByTime(PING_TIMEOUT_MS);
    await expect(detected).resolves.toEqual({ kind: 'absent' });
  });
});
