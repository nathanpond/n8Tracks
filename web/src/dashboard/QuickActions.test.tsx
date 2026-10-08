import { MantineProvider } from '@mantine/core';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { MediaStatus } from '../api/media';
import type { Bridge, BridgeMessage, BridgeReply } from '../extension/bridge';
import { BridgeContext } from '../extension/bridgeContext';
import { fakeTimeouts } from '../test/fakeClock';
import { healthyReport, jsonResponse, requestPath, stubFetch } from '../test/helpers';
import {
  OPEN_SYNC_TIMEOUT_MS,
  PAIRING_ADDRESS,
  QuickActions,
  SCAN_ACTIVE_POLL_MS,
  SCAN_IDLE_POLL_MS,
} from './QuickActions';

const JOB = '0199b1a0-7000-7000-9000-0000000000j1';
const OTHER_JOB = '0199b1a0-7000-7000-9000-0000000000j2';

function status(activeScanJobId: string | null): MediaStatus {
  return {
    mount: { state: 'available', since: null, path: '/media' },
    counts: { total: 0, available: 0, missing: 0, associated: 0, unmatched: 0 },
    lastScan: null,
    lastSuccessfulScan: null,
    activeScanJobId,
    schedule: { enabled: true, intervalMinutes: 15 },
    nextScheduledScan: null,
    majorityMissingWarning: false,
  };
}

const LAST_SONG = {
  song: { id: '0199b1a0-0000-7000-8000-000000000003', shortcode: 'n8-3', title: 'Night Drive' },
  deleted: false,
};

/**
 * A fake n8Tracks: `server.active` is the scan the media status names; a scan start answers
 * `server.start`; the last Song is `server.last`.
 */
function serve(options: { active?: string | null; last?: unknown } = {}) {
  const server = {
    active: options.active ?? null,
    last: options.last ?? LAST_SONG,
    start: { jobId: JOB, alreadyInProgress: false } as unknown,
    starts: 0,
    statusReads: 0,
  };
  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/media/status')) {
      server.statusReads += 1;
      return Promise.resolve(jsonResponse(200, status(server.active)));
    }
    if (method === 'POST' && path.endsWith('/api/v1/media/scans')) {
      server.starts += 1;
      return Promise.resolve(jsonResponse(202, server.start));
    }
    if (path.endsWith('/api/v1/settings/last-song')) {
      return Promise.resolve(jsonResponse(200, server.last));
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return server;
}

/** A fake extension: `answers` by message type; a type it has no answer for times out (null). */
function fakeBridge(answers: Record<string, BridgeReply>) {
  const sent: { message: BridgeMessage; timeoutMs: number | undefined }[] = [];
  const bridge: Bridge = {
    detect: () => Promise.resolve({ kind: 'absent' }),
    send: (message, timeoutMs) => {
      sent.push({ message, timeoutMs });
      return Promise.resolve(answers[message.type] ?? null);
    },
  };
  return { bridge, types: () => sent.map((entry) => entry.message.type), sent };
}

const PONG: BridgeReply = { type: 'pong', connection: { status: 'connected' } };

function renderActions(bridge: Bridge = fakeBridge({}).bridge) {
  return render(
    <MantineProvider>
      <MemoryRouter>
        <BridgeContext.Provider value={bridge}>
          <QuickActions />
        </BridgeContext.Provider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

function actions() {
  return screen.getByRole('region', { name: 'Quick actions' });
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('New Song', () => {
  it('opens the New Song dialog', async () => {
    serve();
    const user = userEvent.setup();
    renderActions();

    await user.click(within(actions()).getByRole('button', { name: 'New Song' }));

    expect(await screen.findByRole('dialog', { name: 'New Song' })).toBeInTheDocument();
  });
});

describe('Scan Library', () => {
  it('starts a scan, says so, and stays disabled until the scan has finished', async () => {
    const { advanceTimers, advanceTimersAsync } = fakeTimeouts();
    const server = serve();
    const user = userEvent.setup({ advanceTimers });
    renderActions();
    const button = within(actions()).getByRole('button', { name: 'Scan Library' });
    await waitFor(() => {
      expect(server.statusReads).toBe(1);
    });
    expect(button).toBeEnabled();

    server.active = JOB;
    await user.click(button);

    await waitFor(() => {
      expect(screen.getByTestId('scan-library-notice')).toHaveTextContent(
        'The library scan started.',
      );
    });
    expect(server.starts).toBe(1);
    expect(button).toBeDisabled();
    expect(screen.getByTestId('scan-library-reason')).toHaveTextContent(
      'A library scan is queued or running.',
    );
    expect(button).toHaveAccessibleDescription('A library scan is queued or running.');

    // Read every five seconds while it runs: still running, still disabled.
    await advanceTimersAsync(SCAN_ACTIVE_POLL_MS);
    expect(server.statusReads).toBe(2);
    expect(button).toBeDisabled();

    server.active = null;
    await advanceTimersAsync(SCAN_ACTIVE_POLL_MS);
    await waitFor(() => {
      expect(button).toBeEnabled();
    });
    expect(screen.getByTestId('scan-library-notice')).toHaveTextContent(
      'The library scan finished.',
    );
    expect(screen.queryByTestId('scan-library-reason')).not.toBeInTheDocument();
  });

  it('is disabled, saying why, while a scan started elsewhere is queued or running, and reads every 30 seconds while idle', async () => {
    const { advanceTimersAsync } = fakeTimeouts();
    const server = serve();
    renderActions();
    const button = within(actions()).getByRole('button', { name: 'Scan Library' });
    await waitFor(() => {
      expect(server.statusReads).toBe(1);
    });

    server.active = OTHER_JOB;
    await advanceTimersAsync(SCAN_IDLE_POLL_MS - 1);
    expect(server.statusReads).toBe(1);
    await advanceTimersAsync(1);
    await waitFor(() => {
      expect(button).toBeDisabled();
    });
    expect(screen.getByTestId('scan-library-reason')).toHaveTextContent(
      'A library scan is queued or running.',
    );

    // Complement: once it ends, it is enabled again.
    server.active = null;
    await advanceTimersAsync(SCAN_ACTIVE_POLL_MS);
    await waitFor(() => {
      expect(button).toBeEnabled();
    });
  });

  it('reports a scan already running when pressed before the dashboard has noticed it', async () => {
    const server = serve();
    server.start = { jobId: OTHER_JOB, alreadyInProgress: true };
    const user = userEvent.setup();
    renderActions();
    const button = within(actions()).getByRole('button', { name: 'Scan Library' });
    await waitFor(() => {
      expect(server.statusReads).toBe(1);
    });

    await user.click(button);

    await waitFor(() => {
      expect(screen.getByTestId('scan-library-notice')).toHaveTextContent(
        'A library scan is already running.',
      );
    });
    expect(button).toBeDisabled();
  });
});

describe('Sync with Suno', () => {
  it('asks the extension to open the Suno library on its Sync view, after a ping', async () => {
    serve();
    const extension = fakeBridge({ ping: PONG, 'open-sync': { type: 'open-sync', ok: true } });
    const user = userEvent.setup();
    renderActions(extension.bridge);

    await user.click(within(actions()).getByRole('button', { name: 'Sync with Suno' }));

    await waitFor(() => {
      expect(screen.getByTestId('sync-with-suno-result')).toHaveTextContent(
        'Your Suno library opened in a new tab, with the extension’s panel on its Sync view.',
      );
    });
    expect(extension.types()).toEqual(['ping', 'open-sync']);
    expect(extension.sent[1]?.timeoutMs).toBe(OPEN_SYNC_TIMEOUT_MS);
    expect(screen.queryByTestId('sync-not-connected')).not.toBeInTheDocument();
  });

  it('shows the warning of a version mismatch and still opens', async () => {
    serve();
    const extension = fakeBridge({
      ping: PONG,
      'open-sync': {
        type: 'open-sync',
        ok: true,
        warning: 'Extension 0.1.0 is older than n8Tracks 0.2.0: update the extension.',
      },
    });
    const user = userEvent.setup();
    renderActions(extension.bridge);

    await user.click(within(actions()).getByRole('button', { name: 'Sync with Suno' }));

    expect(await screen.findByTestId('sync-warning')).toHaveTextContent(
      'Extension 0.1.0 is older than n8Tracks 0.2.0: update the extension.',
    );
    expect(screen.getByTestId('sync-with-suno-result')).toHaveTextContent(
      'Your Suno library opened in a new tab',
    );
  });

  it('sends no open-sync when the extension does not answer the ping, and links to the pairing instructions', async () => {
    serve();
    const extension = fakeBridge({ 'open-sync': { type: 'open-sync', ok: true } });
    const user = userEvent.setup();
    renderActions(extension.bridge);

    await user.click(within(actions()).getByRole('button', { name: 'Sync with Suno' }));

    const note = await screen.findByTestId('sync-not-connected');
    expect(note).toHaveAttribute('data-problem', 'absent');
    expect(note).toHaveTextContent('The n8Tracks extension did not answer');
    expect(
      within(note).getByRole('link', { name: 'Pair the extension in Settings → Credentials' }),
    ).toHaveAttribute('href', PAIRING_ADDRESS);
    expect(extension.types()).toEqual(['ping']);
  });

  it.each([
    ['unpaired', 'The extension is not paired with this n8Tracks.'],
    ['revoked', 'This n8Tracks no longer accepts the extension’s credential: it was revoked.'],
    ['missing_scope', 'The extension’s credential lacks the suno.sync scope, which syncing needs.'],
    ['unreachable', 'The extension cannot reach this n8Tracks right now.'],
  ])('says the extension is not connected when it answers %s', async (reason, sentence) => {
    serve();
    const extension = fakeBridge({
      ping: PONG,
      'open-sync': { type: 'open-sync', ok: false, reason, message: 'from the extension' },
    });
    const user = userEvent.setup();
    renderActions(extension.bridge);

    await user.click(within(actions()).getByRole('button', { name: 'Sync with Suno' }));

    const note = await screen.findByTestId('sync-not-connected');
    expect(note).toHaveAttribute('data-problem', reason);
    expect(note).toHaveTextContent(sentence);
    expect(within(note).getByRole('link')).toHaveAttribute('href', PAIRING_ADDRESS);
  });

  it('says so when open-sync is not answered in time, or by an extension that does not know it', async () => {
    serve();
    const silent = fakeBridge({ ping: PONG });
    const user = userEvent.setup();
    const { unmount } = renderActions(silent.bridge);

    await user.click(within(actions()).getByRole('button', { name: 'Sync with Suno' }));
    expect(await screen.findByTestId('sync-not-connected')).toHaveAttribute(
      'data-problem',
      'timeout',
    );
    unmount();

    const older = fakeBridge({
      ping: PONG,
      'open-sync': { type: 'error', error: 'unknown_type', message: 'nope' },
    });
    renderActions(older.bridge);
    await user.click(within(actions()).getByRole('button', { name: 'Sync with Suno' }));
    expect(await screen.findByTestId('sync-not-connected')).toHaveTextContent(
      'This version of the extension cannot open its Sync view. Update the extension.',
    );
  });
});

describe('Open last Song', () => {
  it('names the Song opened last and opens it', async () => {
    serve();
    renderActions();

    const link = await within(actions()).findByRole('link', {
      name: 'Open last Song: Night Drive',
    });
    expect(link).toHaveAttribute('href', '/songs/n8-3');
    expect(link).toHaveTextContent('Open last Song');
    expect(screen.getByTestId('last-song')).toHaveTextContent('Night Drive n8-3');
  });

  it.each([
    [{ song: null, deleted: false }, 'No Song has been opened yet.'],
    [{ song: null, deleted: true }, 'The last Song you opened has been deleted.'],
  ])('is disabled, saying why, when there is none or it was deleted', async (last, reason) => {
    serve({ last });
    renderActions();

    await waitFor(() => {
      expect(screen.getByTestId('last-song-reason')).toHaveTextContent(reason);
    });
    const button = within(actions()).getByRole('button', { name: 'Open last Song' });
    expect(button).toBeDisabled();
    expect(button).toHaveAccessibleDescription(reason);
    expect(within(actions()).queryByRole('link', { name: /Open last Song/ })).toBeNull();
  });
});
