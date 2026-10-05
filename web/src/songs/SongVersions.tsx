import { Anchor, Badge, Button, Grid, Group, Paper, Stack, Text, Title } from '@mantine/core';
import { useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import type { Song } from '../api/songs';
import { useConfiguredTimeZone } from '../api/timeZone';
import { setCurrentVersion, type Version } from '../api/versions';
import { CreateVersionDialog } from './CreateVersionDialog';
import { RelativeTime } from './SongParts';
import { VersionTree } from './VersionTree';

const FAILED_MESSAGE =
  'Not changed: n8Tracks did not answer as expected. Check that it is running and try again.';

/** The selected Version: its number, name, and notes, and what can be done with it. */
function VersionPane({
  version,
  onCreateFrom,
  onMakeCurrent,
}: {
  version: Version;
  onCreateFrom: () => void;
  onMakeCurrent: () => Promise<boolean>;
}) {
  const timeZone = useConfiguredTimeZone();
  const [switching, setSwitching] = useState(false);
  const [failed, setFailed] = useState(false);

  const makeCurrent = async () => {
    setSwitching(true);
    setFailed(false);
    const done = await onMakeCurrent();
    setSwitching(false);
    setFailed(!done);
  };

  return (
    <Paper p="md" withBorder component="section" aria-labelledby="version-heading">
      <Stack gap="sm">
        <Group gap="sm" align="center" wrap="wrap">
          <Title order={3} size="h4" id="version-heading">
            Version {version.number}
          </Title>
          {version.current && (
            <Badge variant="filled" radius="sm" tt="none">
              Current working Version
            </Badge>
          )}
          {version.archived && (
            <Badge variant="outline" color="gray" radius="sm" tt="none">
              Archived
            </Badge>
          )}
        </Group>
        <Text ff="monospace" size="sm" c="var(--n8-color-secondary-text)">
          {version.shortcode}
        </Text>
        <Text style={{ overflowWrap: 'anywhere' }}>
          {version.name ?? (
            <Text span c="var(--n8-color-secondary-text)">
              No name.
            </Text>
          )}
        </Text>
        {version.notes !== null && (
          <Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{version.notes}</Text>
        )}
        <Text size="sm" c="var(--n8-color-secondary-text)">
          Created <RelativeTime utc={version.createdAt} timeZone={timeZone} />
        </Text>
        <Group gap="sm">
          <Button onClick={onCreateFrom}>Create New Version From {version.number}</Button>
          {!version.current && (
            <Button
              variant="default"
              loading={switching}
              onClick={() => {
                void makeCurrent();
              }}
            >
              Make current
            </Button>
          )}
        </Group>
        {failed && (
          <Text size="sm" c="var(--mantine-color-error)" role="alert">
            {FAILED_MESSAGE}
          </Text>
        )}
      </Stack>
    </Paper>
  );
}

/**
 * A Song's Versions: the tree on the left and the selected Version on the right. The selection is
 * the URL's (`/songs/<shortcode>/v/<number>`), or the current working Version when the URL names
 * none. A link to an archived Version turns "Show archived" on for this visit. Creating a Version
 * makes it current and selects it; any Version can be made current. `loaded` is the list as first
 * read; the page keeps its own copy from then on.
 */
export function SongVersions({
  song,
  loaded,
  onSong,
}: {
  song: Song;
  loaded: Version[];
  onSong: (song: Song) => void;
}) {
  const { number } = useParams();
  const location: { state: unknown } = useLocation();
  const navigate = useNavigate();
  const [versions, setVersions] = useState(loaded);
  const [showArchived, setShowArchived] = useState(false);
  const [archivedLinkSeen, setArchivedLinkSeen] = useState<string | undefined>();
  const [source, setSource] = useState<Version | undefined>();

  const selected =
    number === undefined
      ? versions.find((version) => version.current)
      : versions.find((version) => version.number === number);

  // A link to an archived Version shows it in the tree: "Show archived" goes on, once per link.
  if (
    selected?.archived === true &&
    !selected.current &&
    !showArchived &&
    archivedLinkSeen !== selected.id
  ) {
    setArchivedLinkSeen(selected.id);
    setShowArchived(true);
  }

  const linkTo = (version: Version) => `/songs/${song.shortcode}/v/${version.number}`;

  const markCurrent = (currentId: string) => {
    setVersions((previous) =>
      previous.map((version) => ({ ...version, current: version.id === currentId })),
    );
  };

  const created = (version: Version) => {
    setSource(undefined);
    setVersions((previous) => [
      ...previous.map((other) => ({ ...other, current: false })),
      version,
    ]);
    onSong({
      ...song,
      currentVersion: { id: version.id, number: version.number, shortcode: version.shortcode },
      versionCount: song.versionCount + 1,
    });
    void navigate(linkTo(version), { state: location.state });
  };

  const makeCurrent = async (version: Version) => {
    const result = await setCurrentVersion(song.id, version.id);
    if (result.kind !== 'saved') {
      return false;
    }
    onSong(result.song);
    markCurrent(result.song.currentVersion.id);
    return true;
  };

  return (
    <Grid gap="lg">
      <Grid.Col span={{ base: 12, sm: 4 }}>
        <VersionTree
          versions={versions}
          selectedId={selected?.id}
          showArchived={showArchived}
          onShowArchived={setShowArchived}
          linkTo={linkTo}
          linkState={location.state}
        />
      </Grid.Col>
      <Grid.Col span={{ base: 12, sm: 8 }}>
        {selected === undefined ? (
          <Paper p="md" withBorder component="section" aria-labelledby="version-heading">
            <Stack gap="sm">
              <Title order={3} size="h4" id="version-heading">
                Version not found
              </Title>
              <Text>
                {song.shortcode} has no Version {number}.
              </Text>
              <Anchor component={Link} to={`/songs/${song.shortcode}`} state={location.state}>
                Open {song.shortcode} at its current Version
              </Anchor>
            </Stack>
          </Paper>
        ) : (
          <VersionPane
            key={selected.id}
            version={selected}
            onCreateFrom={() => {
              setSource(selected);
            }}
            onMakeCurrent={() => makeCurrent(selected)}
          />
        )}
      </Grid.Col>
      <CreateVersionDialog
        songId={song.id}
        source={source}
        onClose={() => {
          setSource(undefined);
        }}
        onCreated={created}
      />
    </Grid>
  );
}
