import { Button, Checkbox, Code, Loader, Stack, Table, Text } from '@mantine/core';
import { useState } from 'react';
import {
  changeText,
  membersText,
  readText,
  setPlaylistMemberships,
  usePlaylistMemberships,
} from '../api/sunoPlaylistMemberships';
import { Notice } from '../components/Notice';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/** How many of a playlist's not-imported songs are named before the rest are counted. */
const NAMED_NOT_IMPORTED = 10;

/**
 * The review's "Playlists" of a playlist sync (#449): each playlist the sync read, its songs as read
 * from its page (in part, when Suno showed fewer than the playlist's count), how they compare with what
 * n8Tracks recorded, and which of them n8Tracks has and which are not imported (named by Suno ID; a
 * sync of the library or the song's workspace brings them in). Each is ticked to record its songs at
 * Confirm, a complete read by default, and can be unticked to leave what n8Tracks has as it is.
 * `revision` is the export's, as the review read it; `onChanged`, `onConflict`, and `onEnded` as for
 * the Suno state changes.
 */
export function PlaylistMemberships({
  exportId,
  revision,
  onChanged,
  onConflict,
  onEnded,
}: {
  exportId: string;
  revision: number;
  onChanged: () => void;
  onConflict: () => void;
  onEnded: () => void;
}) {
  const load = usePlaylistMemberships(exportId);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string>();
  const [saved, setSaved] = useState(revision);

  if (load.state.phase === 'loading') {
    return <Loader aria-label="Loading the playlists" />;
  }
  if (load.state.phase !== 'ready') {
    return (
      <Notice title="The playlists could not be loaded">
        <Text>{FAILED_MESSAGE}</Text>
        <div>
          <Button variant="default" size="xs" onClick={load.reload}>
            Try again
          </Button>
        </div>
      </Notice>
    );
  }
  const rows = load.state.data.items;
  if (rows.length === 0) {
    return null;
  }

  const toggle = async (sunoId: string, record: boolean) => {
    setBusy(true);
    setMessage(undefined);
    const result = await setPlaylistMemberships(
      exportId,
      Math.max(saved, revision),
      [sunoId],
      record,
    );
    setBusy(false);
    switch (result.kind) {
      case 'changed':
        setSaved(result.revision);
        setMessage(
          record
            ? 'Its songs will be recorded at Confirm.'
            : 'Its songs are left as n8Tracks has them.',
        );
        load.reload();
        onChanged();
        return;
      case 'conflict':
        onConflict();
        return;
      case 'not-ready':
        onEnded();
        return;
      default:
        setMessage(`Nothing was changed. ${FAILED_MESSAGE}`);
    }
  };

  return (
    <Stack gap="sm" component="section" aria-labelledby="playlist-memberships-title">
      <Text fw={600} id="playlist-memberships-title">
        Playlists
      </Text>
      <Text size="sm">
        This sync read which songs each playlist holds. Songs not imported are named by their Suno
        ID; sync your library or their workspace to bring them in. Nothing changes in Suno.
      </Text>
      <div role="status">
        {message !== undefined && <Text data-testid="playlist-membership-message">{message}</Text>}
      </div>
      <Table.ScrollContainer minWidth={640}>
        <Table withTableBorder aria-label="Playlists read">
          <Table.Thead>
            <Table.Tr>
              <Table.Th scope="col" w={110}>
                Record songs
              </Table.Th>
              <Table.Th scope="col">Playlist</Table.Th>
              <Table.Th scope="col">Songs</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {rows.map((row) => {
              const name = row.name === '' ? row.sunoId : row.name;
              const named = row.notImported.slice(0, NAMED_NOT_IMPORTED);
              return (
                <Table.Tr key={row.sunoId} data-playlist={row.sunoId} data-change={row.change}>
                  <Table.Td>
                    <Checkbox
                      aria-label={`Record the songs of ${name}`}
                      checked={row.record}
                      disabled={busy}
                      onChange={(event) => {
                        void toggle(row.sunoId, event.currentTarget.checked);
                      }}
                    />
                  </Table.Td>
                  <Table.Th scope="row" fw="normal">
                    <Text size="sm">{name}</Text>
                  </Table.Th>
                  <Table.Td>
                    <Text size="sm" data-testid="playlist-read">
                      {readText(row)}
                      {!row.complete && !row.chosen && ' Not recorded unless you tick it.'}
                    </Text>
                    <Text size="sm" data-testid="playlist-change">
                      {changeText(row)}
                    </Text>
                    <Text size="sm" data-testid="playlist-members">
                      {membersText(row)}
                    </Text>
                    {named.length > 0 && (
                      <Text size="xs" data-testid="playlist-not-imported">
                        Not imported:{' '}
                        {named.map((id, index) => (
                          <span key={id}>
                            {index > 0 && ', '}
                            <Code>{id}</Code>
                          </span>
                        ))}
                        {row.notImported.length > named.length &&
                          ` and ${String(row.notImported.length - named.length)} more`}
                      </Text>
                    )}
                  </Table.Td>
                </Table.Tr>
              );
            })}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
    </Stack>
  );
}
