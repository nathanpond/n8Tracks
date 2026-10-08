import { Anchor, Button, Paper, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { Link, useNavigate } from 'react-router';
import { apiFetch } from '../api/client';
import { useLastSong, type LastSong } from '../api/dashboardSettings';
import { isMediaStatus, MEDIA_STATUS_PATH, startScan } from '../api/media';
import { body } from '../api/songs';
import { PING_TIMEOUT_MS, type Bridge, type BridgeReply } from '../extension/bridge';
import { useBridge } from '../extension/bridgeContext';
import { NewSongDialog } from '../songs/NewSongDialog';

/** How often the dashboard reads the media status while no scan is queued or running. */
export const SCAN_IDLE_POLL_MS = 30_000;
/** How often it reads it while Scan Library is disabled, until the scan has finished. */
export const SCAN_ACTIVE_POLL_MS = 5_000;
/** How long Sync with Suno waits for the extension to answer `open-sync`. */
export const OPEN_SYNC_TIMEOUT_MS = 2_000;
/** Where an extension credential is created: the pairing instructions. */
export const PAIRING_ADDRESS = '/settings/credentials';

/** One action: its control, and what is said about it below (a reason, a notice, a result). */
function Action({ children, note }: { children: ReactNode; note?: ReactNode }) {
  return (
    <Stack gap={4} align="flex-start">
      {children}
      {note}
    </Stack>
  );
}

/**
 * Scan Library: whether a scan is queued or running (read from the media status every
 * {@link SCAN_IDLE_POLL_MS}, so a scan started elsewhere disables the button, and every
 * {@link SCAN_ACTIVE_POLL_MS} while one is), and what pressing it came to.
 */
function useScanLibrary() {
  const [statusActive, setStatusActive] = useState(false);
  const [startedJob, setStartedJob] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const startedAt = useRef(0);
  const startedRef = useRef<string | null>(null);
  const busy = statusActive || startedJob !== null;

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const read = async () => {
      const readAt = Date.now();
      try {
        const response = await apiFetch(MEDIA_STATUS_PATH, { signal: controller.signal });
        const answer = await body(response);
        if (controller.signal.aborted) {
          return;
        }
        if (response.ok && isMediaStatus(answer)) {
          const active = answer.activeScanJobId;
          setStatusActive(active !== null);
          // The scan this page started has finished once a read made after it no longer names it.
          const job = startedRef.current;
          if (job !== null && readAt >= startedAt.current && active !== job) {
            startedRef.current = null;
            setStartedJob(null);
            setNotice('The library scan finished.');
          }
        }
      } catch {
        if (controller.signal.aborted) {
          return;
        }
      }
    };
    const loop = async () => {
      await read();
      if (!controller.signal.aborted) {
        timer = setTimeout(
          () => {
            void loop();
          },
          busy ? SCAN_ACTIVE_POLL_MS : SCAN_IDLE_POLL_MS,
        );
      }
    };
    // A change of busy restarts the loop at the new pace; the first read of the new pace waits.
    timer = setTimeout(
      () => {
        void loop();
      },
      busy ? SCAN_ACTIVE_POLL_MS : 0,
    );
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [busy]);

  const start = useCallback(async () => {
    setStarting(true);
    setNotice(null);
    const result = await startScan();
    setStarting(false);
    if (result.kind === 'failed') {
      setNotice('The scan could not be started. Try again.');
      return;
    }
    startedAt.current = Date.now();
    startedRef.current = result.jobId;
    setStartedJob(result.jobId);
    setNotice(
      result.alreadyInProgress
        ? 'A library scan is already running. Scan Library is available again when it finishes.'
        : 'The library scan started. Scan Library is available again when it finishes.',
    );
  }, []);

  return { busy, starting, notice, start };
}

/** Why Sync with Suno opened nothing, in the page's words. */
type SyncProblem =
  'absent' | 'timeout' | 'unpaired' | 'revoked' | 'missing_scope' | 'unreachable' | 'unsupported';

type SyncState =
  | { kind: 'idle' }
  | { kind: 'checking' }
  | { kind: 'opened'; warning: string | null }
  | { kind: 'not-connected'; problem: SyncProblem };

const SYNC_PROBLEMS: Record<SyncProblem, string> = {
  absent:
    'The n8Tracks extension did not answer: it is not installed in this browser, or not paired with this n8Tracks.',
  timeout: 'The extension did not answer in time, so your Suno library was not opened.',
  unpaired: 'The extension is not paired with this n8Tracks.',
  revoked: 'This n8Tracks no longer accepts the extension’s credential: it was revoked.',
  missing_scope: 'The extension’s credential lacks the suno.sync scope, which syncing needs.',
  unreachable: 'The extension cannot reach this n8Tracks right now.',
  unsupported: 'This version of the extension cannot open its Sync view. Update the extension.',
};

/** The problem an `open-sync` answer names; null when it opened the library. */
function problemOf(reply: BridgeReply): SyncProblem | null {
  if (reply === null) {
    return 'timeout';
  }
  if (reply.type !== 'open-sync') {
    return 'unsupported';
  }
  if (reply.ok === true) {
    return null;
  }
  switch (reply.reason) {
    case 'unpaired':
    case 'revoked':
    case 'missing_scope':
    case 'unreachable':
      return reply.reason;
    default:
      return 'unpaired';
  }
}

/**
 * Sync with Suno: pings the relay first and asks nothing more when it does not answer; otherwise asks
 * the extension (`open-sync`) to open the user's Suno library with its panel on the Sync view, which
 * starts nothing by itself. The extension checks its pairing and says why when it cannot.
 */
function useSyncWithSuno(bridge: Bridge) {
  const [state, setState] = useState<SyncState>({ kind: 'idle' });
  const run = useCallback(async () => {
    setState({ kind: 'checking' });
    const pong = await bridge.send({ type: 'ping' }, PING_TIMEOUT_MS);
    if (pong?.type !== 'pong') {
      setState({ kind: 'not-connected', problem: 'absent' });
      return;
    }
    const reply = await bridge.send({ type: 'open-sync' }, OPEN_SYNC_TIMEOUT_MS);
    const problem = problemOf(reply);
    if (problem !== null) {
      setState({ kind: 'not-connected', problem });
      return;
    }
    const warning =
      typeof reply?.warning === 'string' && reply.warning !== '' ? reply.warning : null;
    setState({ kind: 'opened', warning });
  }, [bridge]);
  return { state, run };
}

function SyncNote({ state }: { state: SyncState }) {
  switch (state.kind) {
    case 'idle':
    case 'checking':
      return null;
    case 'opened':
      return (
        <Stack gap={2}>
          <Text size="sm">
            Your Suno library opened in a new tab, with the extension’s panel on its Sync view.
          </Text>
          {state.warning !== null && (
            <Text size="sm" data-testid="sync-warning">
              {state.warning}
            </Text>
          )}
        </Stack>
      );
    case 'not-connected':
      return (
        <Text size="sm" data-testid="sync-not-connected" data-problem={state.problem}>
          {SYNC_PROBLEMS[state.problem]}{' '}
          <Anchor component={Link} to={PAIRING_ADDRESS} underline="always">
            Pair the extension in Settings → Credentials
          </Anchor>
        </Text>
      );
  }
}

/** Why Open last Song is disabled; null when it is not. */
function lastSongReason(last: LastSong | undefined | 'failed'): string | null {
  if (last === undefined) {
    return 'Reading the last Song you opened…';
  }
  if (last === 'failed') {
    return 'The last Song you opened could not be read.';
  }
  switch (last.kind) {
    case 'none':
      return 'No Song has been opened yet.';
    case 'deleted':
      return 'The last Song you opened has been deleted.';
    case 'song':
      return null;
  }
}

/**
 * The dashboard's quick actions (#230), always shown above the sections: New Song (the New Song
 * dialog), Scan Library (disabled, saying so, while a scan is queued or running), Sync with Suno (the
 * extension's Sync view on the user's Suno library, or why not with the pairing instructions), and
 * Open last Song (named; disabled, saying why, when there is none or it was deleted).
 */
export function QuickActions() {
  const navigate = useNavigate();
  const bridge = useBridge();
  const [creating, setCreating] = useState(false);
  const scan = useScanLibrary();
  const sync = useSyncWithSuno(bridge);
  const last = useLastSong();
  const lastReason = lastSongReason(last);
  const song = last !== undefined && last !== 'failed' && last.kind === 'song' ? last : null;

  return (
    <Paper
      component="section"
      aria-labelledby="dashboard-quick-actions"
      p="md"
      withBorder
      data-testid="quick-actions"
    >
      <Stack gap="sm">
        <Title order={3} id="dashboard-quick-actions">
          Quick actions
        </Title>
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="md">
          <Action>
            <Button
              onClick={() => {
                setCreating(true);
              }}
            >
              New Song
            </Button>
          </Action>
          <Action
            note={
              <>
                {scan.busy && (
                  <Text size="sm" id="scan-library-reason" data-testid="scan-library-reason">
                    A library scan is queued or running.
                  </Text>
                )}
                <Text size="sm" role="status" data-testid="scan-library-notice">
                  {scan.notice ?? ''}
                </Text>
              </>
            }
          >
            <Button
              variant="default"
              disabled={scan.busy}
              loading={scan.starting}
              aria-describedby={scan.busy ? 'scan-library-reason' : undefined}
              onClick={() => {
                void scan.start();
              }}
            >
              Scan Library
            </Button>
          </Action>
          <Action
            note={
              <div role="status" data-testid="sync-with-suno-result">
                <SyncNote state={sync.state} />
              </div>
            }
          >
            <Button
              variant="default"
              loading={sync.state.kind === 'checking'}
              onClick={() => {
                void sync.run();
              }}
            >
              Sync with Suno
            </Button>
          </Action>
          <Action
            note={
              song !== null ? (
                <Text size="sm" data-testid="last-song">
                  {song.title}{' '}
                  <Text span size="sm" c="var(--n8-color-secondary-text)">
                    {song.shortcode}
                  </Text>
                </Text>
              ) : (
                <Text size="sm" id="last-song-reason" data-testid="last-song-reason">
                  {lastReason}
                </Text>
              )
            }
          >
            {song !== null ? (
              <Button
                variant="default"
                component={Link}
                to={`/songs/${song.shortcode}`}
                aria-label={`Open last Song: ${song.title}`}
              >
                Open last Song
              </Button>
            ) : (
              <Button variant="default" disabled aria-describedby="last-song-reason">
                Open last Song
              </Button>
            )}
          </Action>
        </SimpleGrid>
      </Stack>
      <NewSongDialog
        opened={creating}
        onClose={() => {
          setCreating(false);
        }}
        onCreated={(created) => {
          setCreating(false);
          void navigate(`/songs/${created.shortcode}`);
        }}
      />
    </Paper>
  );
}
