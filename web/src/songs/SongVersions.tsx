import { Anchor, Button, Grid, Group, Paper, Stack, Text, Title } from '@mantine/core';
import { useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import type { Song } from '../api/songs';
import { setCurrentVersion, setVersionArchived, type Version } from '../api/versions';
import { CreateVersionDialog } from './CreateVersionDialog';
import { VersionDetails } from './VersionDetails';
import { VersionTree, type VersionActions } from './VersionTree';

const FAILED_MESSAGE =
  'Not changed: n8Tracks did not answer as expected. Check that it is running and try again.';

/** Where this browser keeps the "Show archived" choice. */
const SHOW_ARCHIVED_STORAGE_KEY = 'n8tracks-show-archived-versions';

/** The "Show archived" choice stored in this browser; off when there is none or storage is unavailable. */
function storedShowArchived(): boolean {
  try {
    return window.localStorage.getItem(SHOW_ARCHIVED_STORAGE_KEY) === 'true';
  } catch {
    return false;
  }
}

function storeShowArchived(show: boolean) {
  try {
    window.localStorage.setItem(SHOW_ARCHIVED_STORAGE_KEY, String(show));
  } catch {
    // Storage may be unavailable (private mode, blocked site data): the choice lasts this visit.
  }
}

/** What the page tells the user after an action: an archive that can be undone, or a failure. */
type Notice = { kind: 'archived'; version: Version } | { kind: 'failed' } | undefined;

/**
 * A Song's Versions: the tree on the left and the selected Version on the right. The selection is
 * the URL's (`/songs/<shortcode>/v/<number>`), or the current working Version when the URL names
 * none or names one that is hidden. "Show archived" is remembered per browser; a link to an
 * archived Version turns it on for this visit. Creating a Version makes it current and selects it;
 * any Version can be made current, archived, or unarchived from its actions menu in the tree or
 * from the selected Version's header. Archiving happens at once, with an Undo notice. `loaded` is
 * the list as first read; the page keeps its own copy from then on.
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
  const [showArchived, setShowArchived] = useState(storedShowArchived);
  const [seenNumber, setSeenNumber] = useState<string | undefined | null>(null);
  const [source, setSource] = useState<Version | undefined>();
  const [notice, setNotice] = useState<Notice>();
  const [busy, setBusy] = useState(false);

  const current = versions.find((version) => version.current);
  const named =
    number === undefined ? current : versions.find((version) => version.number === number);
  const hiddenNow = (version: Version | undefined, show: boolean) =>
    version !== undefined && version.archived && !version.current && !show;

  // Arriving at a link to an archived Version shows it: "Show archived" goes on for this visit.
  if (number !== seenNumber) {
    setSeenNumber(number);
    if (hiddenNow(named, showArchived)) {
      setShowArchived(true);
    }
  }

  // A selected Version that becomes hidden (archived, or "Show archived" turned off) hands the
  // selection to the current Version.
  const hidden = number !== undefined && hiddenNow(named, showArchived);
  const selected = hidden ? current : named;
  useEffect(() => {
    if (hidden) {
      void navigate(`/songs/${song.shortcode}`, { replace: true, state: location.state });
    }
  }, [hidden, navigate, song.shortcode, location.state]);

  const linkTo = (version: Version) => `/songs/${song.shortcode}/v/${version.number}`;

  const replace = (changed: Version) => {
    setVersions((previous) =>
      previous.map((version) => (version.id === changed.id ? changed : version)),
    );
  };

  const latest = (version: Version) =>
    versions.find((candidate) => candidate.id === version.id) ?? version;

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

  const actions: VersionActions = {
    onCreateFrom: (version) => {
      setSource(latest(version));
    },
    onMakeCurrent: (version) => {
      setBusy(true);
      setNotice(undefined);
      void setCurrentVersion(song.id, version.id).then((result) => {
        setBusy(false);
        if (result.kind !== 'saved') {
          setNotice({ kind: 'failed' });
          return;
        }
        onSong(result.song);
        const currentId = result.song.currentVersion.id;
        setVersions((previous) =>
          previous.map((other) => ({ ...other, current: other.id === currentId })),
        );
      });
    },
    onSetArchived: (version, archived) => {
      setBusy(true);
      setNotice(undefined);
      void setVersionArchived(latest(version), archived).then((result) => {
        setBusy(false);
        if (result.kind !== 'saved') {
          setNotice({ kind: 'failed' });
          return;
        }
        replace(result.version);
        setNotice(archived ? { kind: 'archived', version: result.version } : undefined);
      });
    },
  };

  return (
    <Stack gap="sm">
      {notice?.kind === 'archived' && (
        <Paper p="xs" withBorder role="status">
          <Group gap="sm" justify="space-between" wrap="wrap">
            <Text size="sm">
              Version {notice.version.number} archived.
              {!notice.version.current &&
                !showArchived &&
                ' It is hidden while Show archived is off.'}
            </Text>
            <Group gap="xs">
              <Button
                size="compact-sm"
                variant="default"
                disabled={busy}
                onClick={() => {
                  actions.onSetArchived(notice.version, false);
                }}
              >
                Undo
              </Button>
              <Button
                size="compact-sm"
                variant="subtle"
                onClick={() => {
                  setNotice(undefined);
                }}
              >
                Dismiss
              </Button>
            </Group>
          </Group>
        </Paper>
      )}
      {notice?.kind === 'failed' && (
        <Text size="sm" c="var(--mantine-color-error)" role="alert">
          {FAILED_MESSAGE}
        </Text>
      )}
      <Grid gap="lg">
        <Grid.Col span={{ base: 12, sm: 4 }}>
          <VersionTree
            versions={versions}
            selectedId={selected?.id}
            showArchived={showArchived}
            onShowArchived={(show) => {
              setShowArchived(show);
              storeShowArchived(show);
            }}
            onSelect={(version) => {
              void navigate(linkTo(version), { state: location.state });
            }}
            actions={actions}
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
            <VersionDetails
              key={selected.id}
              version={selected}
              onVersion={replace}
              actions={actions}
              busy={busy}
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
    </Stack>
  );
}
