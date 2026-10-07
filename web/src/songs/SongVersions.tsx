import { Anchor, Button, Grid, Group, Paper, Stack, Text, Title } from '@mantine/core';
import { useCallback, useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import { isNamedBy, useSongGenerations, type Generation } from '../api/generations';
import { movedFromOf, pageFor, resolveReference, stateFor } from '../api/references';
import { readSong, type Song } from '../api/songs';
import {
  readSongVersions,
  setCurrentVersion,
  setVersionArchived,
  type Version,
  type VersionDetail,
  type VersionList,
} from '../api/versions';
import type { EditorText } from '../editor/useSnapshots';
import { GenerationPanel, type GenerationPanelContent } from '../generations/GenerationPanel';
import { DeleteGenerationDialog } from '../generations/DeleteGenerationDialog';
import { RETENTION_DAYS } from '../generations/deletionRules';
import { MoveToNewSongDialog } from '../generations/MoveToNewSongDialog';
import { useGenerationChoices } from '../generations/useGenerationChoices';
import { useRateGeneration, type RatingProblem } from '../generations/useRateGeneration';
import { CreateVersionDialog } from './CreateVersionDialog';
import { DeleteVersionDialog } from './DeleteVersionDialog';
import { VersionDetails } from './VersionDetails';
import { VersionsTable } from './VersionsTable';
import { isListed } from './versionsTableRules';
import { VersionTree, type VersionActions } from './VersionTree';

const FAILED_MESSAGE =
  'Not changed: n8Tracks did not answer as expected. Check that it is running and try again.';

/** What the page says when a rating did not go through. */
function ratingProblemText(problem: RatingProblem): string {
  return problem.kind === 'conflict'
    ? `The rating of ${problem.generation.shortcode} was changed somewhere else at the same time, so yours was not saved. It now shows ${ratingWords(problem.generation.rating)}.`
    : `The rating of ${problem.generation.shortcode} was not saved: n8Tracks did not answer as expected. Check that it is running and try again.`;
}

function ratingWords(rating: number | null): string {
  return rating === null ? 'no rating' : `${String(rating)} of 5 stars`;
}

/** The URL parameter that lists archived Versions in the Versions table. */
export const SHOW_ARCHIVED_PARAMETER = 'archived';

/** The URL parameter that lists archived Generations in the Versions table. */
export const SHOW_ARCHIVED_GENERATIONS_PARAMETER = 'archivedGenerations';

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

/** What the page tells the user after an action: an archive that can be undone, a deletion, or a failure. */
type Notice =
  | { kind: 'archived'; version: Version }
  | { kind: 'deleted'; number: string; current: string; createdBlank: boolean }
  | { kind: 'generation-deleted'; shortcode: string }
  | { kind: 'failed' }
  | undefined;

/** A Version as the tree lists it, from one read with its lyrics and styles. */
function summaryOf(detail: VersionDetail): Version {
  const { id, songId, number, shortcode, name, notes, archived, current } = detail;
  const { createdAt, updatedAt, revision, isFrozen, kind } = detail;
  return {
    id,
    songId,
    number,
    shortcode,
    name,
    notes,
    archived,
    current,
    createdAt,
    updatedAt,
    revision,
    isFrozen,
    kind,
  };
}

/**
 * Whether the Version numbered `number` of `song` was deleted (its shortcode resolves as deleted),
 * asked only while `asking`; undefined until the answer comes.
 */
function useWasDeleted(song: Song, number: string | undefined, asking: boolean) {
  const reference = `${song.shortcode}-v${number ?? ''}`;
  const [answer, setAnswer] = useState<{ reference: string; deleted: boolean } | undefined>();
  useEffect(() => {
    if (!asking || number === undefined) {
      return undefined;
    }
    const controller = new AbortController();
    void resolveReference(reference, controller.signal).then((result) => {
      if (!controller.signal.aborted) {
        setAnswer({
          reference,
          deleted: result.kind === 'found' && result.resolved.status === 'deleted',
        });
      }
    });
    return () => {
      controller.abort();
    };
  }, [asking, number, reference]);
  return answer?.reference === reference ? answer.deleted : undefined;
}

/**
 * A Song's Versions: the tree on the left and the selected Version on the right. The selection is
 * the URL's (`/songs/<shortcode>/v/<number>`), or the current working Version when the URL names
 * none or names one that is hidden. "Show archived" is remembered per browser; a link to an
 * archived Version turns it on for this visit. Creating a Version makes it current and selects it;
 * any Version can be made current, archived, unarchived, or deleted from its actions menu in the
 * tree or from the selected Version's header. Archiving happens at once, with an Undo notice;
 * deleting asks first ({@link DeleteVersionDialog}), then selects the Song's current Version. A
 * deleted Version's descendants stay under a "Deleted Version" placeholder, and its page URL says
 * it was deleted. An editor open on a Version deleted elsewhere hands its unsaved text here, offered
 * as the content of a new Version. `loaded` is the list as first read; the page keeps its own copy
 * from then on.
 */
export function SongVersions({
  song,
  loaded,
  onSong,
}: {
  song: Song;
  loaded: VersionList;
  onSong: (song: Song) => void;
}) {
  const { number: routeNumber, generation: generationReference } = useParams();
  const location: { state: unknown; search: string } = useLocation();
  const navigate = useNavigate();
  const generations = useSongGenerations(song.id);
  const reloadGenerations = generations.reload;
  const [ratingProblem, setRatingProblem] = useState<string>();
  const onRatingProblem = useCallback((problem: RatingProblem) => {
    setRatingProblem(ratingProblemText(problem));
  }, []);
  const rateGeneration = useRateGeneration(generations.update, onRatingProblem, reloadGenerations);
  const rate = (generation: Generation, rating: number | null) => {
    setRatingProblem(undefined);
    rateGeneration(generation, rating);
  };
  const choices = useGenerationChoices({
    song,
    onSong,
    update: generations.update,
    markSelected: generations.markSelected,
    onProblem: setRatingProblem,
  });
  /** The Generation "Create new Song from Generation" is open for (#123). */
  const [moving, setMoving] = useState<Generation | undefined>();
  /** The Generation the delete confirmation is open for (#124). */
  const [deletingGeneration, setDeletingGeneration] = useState<Generation | undefined>();
  const generationActions = {
    onSetState: choices.setState,
    onSelect: choices.select,
    onClearSelection: choices.clear,
    onMoveToNewSong: setMoving,
    onDelete: setDeletingGeneration,
    busy: choices.busy,
  };
  const parameters = new URLSearchParams(location.search);
  const showArchivedVersions = parameters.get(SHOW_ARCHIVED_PARAMETER) === '1';
  const showArchivedGenerations = parameters.get(SHOW_ARCHIVED_GENERATIONS_PARAMETER) === '1';
  const [versions, setVersions] = useState(loaded.items);
  const [placeholders, setPlaceholders] = useState(loaded.deletedPlaceholders);
  const [deleting, setDeleting] = useState<Version | undefined>();
  /** Unsaved lyrics and styles of a Version deleted elsewhere while it was open here. */
  const [carried, setCarried] = useState<{ number: string; text: EditorText } | undefined>();
  const [showArchived, setShowArchived] = useState(storedShowArchived);
  const [seenNumber, setSeenNumber] = useState<string | undefined | null>(null);
  const [source, setSource] = useState<{ version: Version; content?: EditorText } | undefined>();
  const [notice, setNotice] = useState<Notice>();
  const [busy, setBusy] = useState(false);

  const current = versions.find((version) => version.current);
  // A Generation's page (`/songs/<song>/generations/<shortcode>`) shows its Version in the editor.
  const openGeneration =
    generationReference !== undefined && generations.state.phase === 'ready'
      ? generations.state.data.find((generation) => isNamedBy(generation, generationReference))
      : undefined;
  const generationVersion =
    openGeneration === undefined
      ? undefined
      : versions.find((version) => version.id === openGeneration.version.id);
  const number = routeNumber ?? generationVersion?.number;
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
  // selection to the current Version; a Version page's address goes back to the Song's. (A
  // Generation's page keeps its address: its panel stays open.)
  const hiddenSelection = number !== undefined && hiddenNow(named, showArchived);
  const selected = hiddenSelection ? current : named;
  const hidden = routeNumber !== undefined && hiddenSelection;
  const search = location.search;
  useEffect(() => {
    if (hidden) {
      void navigate(`/songs/${song.shortcode}${search}`, { replace: true, state: location.state });
    }
  }, [hidden, navigate, song.shortcode, search, location.state]);

  // The Versions table's choices are the URL's, so they stay as the user moves around the Song.
  const linkTo = (version: Version) => `/songs/${song.shortcode}/v/${version.number}${search}`;
  const generationLink = (generation: Generation) =>
    `/songs/${song.shortcode}/generations/${generation.shortcode}${search}`;
  const wasDeleted = useWasDeleted(song, number, number !== undefined && selected === undefined);

  /** Turns one of the Versions table's choices on or off in the URL, replacing this history entry. */
  const setChoice = useCallback(
    (choices: Record<string, boolean>) => {
      const next = new URLSearchParams(search);
      for (const [name, on] of Object.entries(choices)) {
        if (on) {
          next.set(name, '1');
        } else {
          next.delete(name);
        }
      }
      const text = next.toString();
      void navigate(
        { search: text === '' ? '' : `?${text}` },
        { replace: true, state: location.state },
      );
    },
    [location.state, navigate, search],
  );

  // A link to a Generation the table hides (an archived one, or one of an archived Version) turns
  // on what it takes to list it.
  const needsArchivedGenerations =
    openGeneration?.state === 'archived' && !openGeneration.isSelected && !showArchivedGenerations;
  const needsArchivedVersions =
    generationVersion !== undefined && !isListed(generationVersion, showArchivedVersions);
  useEffect(() => {
    if (needsArchivedGenerations || needsArchivedVersions) {
      setChoice({
        ...(needsArchivedGenerations ? { [SHOW_ARCHIVED_GENERATIONS_PARAMETER]: true } : {}),
        ...(needsArchivedVersions ? { [SHOW_ARCHIVED_PARAMETER]: true } : {}),
      });
    }
  }, [needsArchivedGenerations, needsArchivedVersions, setChoice]);

  const panelContent = ((): GenerationPanelContent => {
    if (generationReference === undefined || generations.state.phase === 'loading') {
      return { kind: 'loading' };
    }
    if (generations.state.phase !== 'ready') {
      return { kind: 'failed' };
    }
    if (openGeneration === undefined) {
      return { kind: 'not-found', reference: generationReference };
    }
    return {
      kind: 'found',
      generation: openGeneration,
      versionNumber: generationVersion?.number ?? '',
      versionLink:
        generationVersion === undefined
          ? `/songs/${song.shortcode}${search}`
          : linkTo(generationVersion),
    };
  })();

  // A Generation's old address, from before it moved to another Song (#123), opens it where it is
  // now, saying so: its old shortcode resolves as moved.
  const missingGeneration = panelContent.kind === 'not-found' ? panelContent.reference : undefined;
  useEffect(() => {
    if (missingGeneration === undefined) {
      return undefined;
    }
    const controller = new AbortController();
    void resolveReference(missingGeneration, controller.signal).then((result) => {
      if (
        !controller.signal.aborted &&
        result.kind === 'found' &&
        result.resolved.status === 'moved'
      ) {
        void navigate(pageFor(result.resolved), {
          replace: true,
          state: stateFor(result.resolved, missingGeneration),
        });
      }
    });
    return () => {
      controller.abort();
    };
  }, [missingGeneration, navigate]);

  /** The panel closes onto the Generation's Version, which the editor already shows. */
  const closePanel = () => {
    void navigate(
      generationVersion === undefined
        ? `/songs/${song.shortcode}${search}`
        : linkTo(generationVersion),
      { state: location.state },
    );
  };

  /** Reads the Versions and the Song again (after a deletion here or elsewhere); false when that fails. */
  const refresh = useCallback(async () => {
    const [list, read] = await Promise.all([readSongVersions(song.id), readSong(song.id)]);
    if (list === undefined || read === undefined) {
      return false;
    }
    setVersions(list.items);
    setPlaceholders(list.deletedPlaceholders);
    onSong(read);
    reloadGenerations();
    return true;
  }, [onSong, song.id, reloadGenerations]);

  const deleted = (version: Version, current: VersionDetail) => {
    setDeleting(undefined);
    const createdBlank = !versions.some((other) => other.id === current.id);
    setNotice({ kind: 'deleted', number: version.number, current: current.number, createdBlank });
    // Until the list is read again, the page's own copy drops the Version and marks the current one.
    setVersions((previous) => [
      ...previous
        .filter((other) => other.id !== version.id && other.id !== current.id)
        .map((other) => ({ ...other, current: false })),
      summaryOf(current),
    ]);
    void refresh();
    void navigate(linkTo(current), { replace: true, state: location.state });
  };

  /** The user's Create in Suno was recorded (#149): the Versions, the Song, and its Generations are read again. */
  const recorded = useCallback(() => {
    void refresh();
  }, [refresh]);

  /** The open Version turned out to be deleted elsewhere: its unsaved text, if any, waits here. */
  const deletedElsewhere = useCallback(
    (version: Version, text: EditorText | undefined) => {
      if (text !== undefined) {
        setCarried({ number: version.number, text });
      }
      // Its own page says it was deleted, and offers the text there.
      void navigate(`/songs/${song.shortcode}/v/${version.number}${search}`, {
        replace: true,
        state: location.state,
      });
      void refresh();
    },
    [location.state, navigate, refresh, search, song.shortcode],
  );

  const replace = (changed: Version) => {
    setVersions((previous) =>
      previous.map((version) => (version.id === changed.id ? changed : version)),
    );
    // The Song says what its current Version creates, so a change of kind there is the Song's too.
    if (changed.id === song.currentVersion.id && changed.kind !== song.currentVersion.kind) {
      onSong({ ...song, currentVersion: { ...song.currentVersion, kind: changed.kind } });
    }
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
      currentVersion: {
        id: version.id,
        number: version.number,
        shortcode: version.shortcode,
        kind: version.kind,
      },
      versionCount: song.versionCount + 1,
    });
    void navigate(linkTo(version), { state: location.state });
  };

  const actions: VersionActions = {
    onCreateFrom: (version, content) => {
      setSource({ version: latest(version), content });
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
    onDelete: (version) => {
      setNotice(undefined);
      setDeleting(latest(version));
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
      {notice?.kind === 'deleted' && (
        <Paper p="xs" withBorder role="status" data-testid="version-deleted">
          <Group gap="sm" justify="space-between" wrap="wrap">
            <Text size="sm">
              Version {notice.number} deleted.{' '}
              {notice.createdBlank
                ? `It was the only Version, so a new blank Version ${notice.current} was created and is current.`
                : `Version ${notice.current} is current.`}
            </Text>
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
        </Paper>
      )}
      {notice?.kind === 'generation-deleted' && (
        <Paper p="xs" withBorder role="status" data-testid="generation-deleted">
          <Group gap="sm" justify="space-between" wrap="wrap">
            <Text size="sm">
              Generation {notice.shortcode} deleted. Nothing in Suno was changed; it can be restored
              for {RETENTION_DAYS} days.
            </Text>
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
            placeholders={placeholders}
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
                  {wasDeleted === true
                    ? `Version ${number ?? ''} was deleted`
                    : 'Version not found'}
                </Title>
                <Text>
                  {wasDeleted === true
                    ? `${song.shortcode}-v${number ?? ''} was deleted. Its number is not used again.`
                    : `${song.shortcode} has no Version ${number ?? ''}.`}
                </Text>
                {carried !== undefined && carried.number === number && current !== undefined && (
                  <Stack gap="xs" data-testid="carried-text">
                    <Text fw={700}>
                      Your unsaved changes to its lyrics and styles could not be saved. They are
                      kept here until you leave the page: start a new Version with them.
                    </Text>
                    <div>
                      <Button
                        onClick={() => {
                          setSource({ version: current, content: carried.text });
                        }}
                      >
                        Create a new Version with my text
                      </Button>
                    </div>
                  </Stack>
                )}
                <Anchor
                  component={Link}
                  to={`/songs/${song.shortcode}${search}`}
                  state={location.state}
                >
                  Open {song.shortcode} at its current Version
                </Anchor>
              </Stack>
            </Paper>
          ) : (
            <VersionDetails
              key={selected.id}
              version={selected}
              onVersion={replace}
              onDeletedElsewhere={deletedElsewhere}
              onRecorded={recorded}
              actions={actions}
              busy={busy}
            />
          )}
        </Grid.Col>
        <CreateVersionDialog
          songId={song.id}
          source={source?.version}
          content={source?.content}
          onClose={() => {
            setSource(undefined);
          }}
          onCreated={(version) => {
            setCarried(undefined);
            created(version);
          }}
        />
        <DeleteVersionDialog
          version={deleting}
          onClose={() => {
            setDeleting(undefined);
          }}
          onDeleted={deleted}
        />
      </Grid>
      {ratingProblem !== undefined && (
        <Text size="sm" c="var(--mantine-color-error)" role="alert" data-testid="rating-problem">
          {ratingProblem}
        </Text>
      )}
      <VersionsTable
        versions={versions}
        generations={generations.state}
        onRetry={reloadGenerations}
        selectedId={selected?.id}
        openGenerationId={openGeneration?.id}
        revealVersionId={openGeneration?.version.id}
        showArchived={showArchivedVersions}
        showArchivedGenerations={showArchivedGenerations}
        onShowArchived={(show) => {
          setChoice({ [SHOW_ARCHIVED_PARAMETER]: show });
        }}
        onShowArchivedGenerations={(show) => {
          setChoice({ [SHOW_ARCHIVED_GENERATIONS_PARAMETER]: show });
        }}
        versionLink={linkTo}
        generationLink={generationLink}
        onRate={rate}
        actions={generationActions}
      />
      <GenerationPanel
        opened={generationReference !== undefined}
        content={panelContent}
        onClose={closePanel}
        onRate={rate}
        update={generations.update}
        actions={generationActions}
        problem={ratingProblem}
        movedFrom={movedFromOf(location.state)}
      />
      {choices.dialog}
      <DeleteGenerationDialog
        generation={deletingGeneration}
        song={song}
        onClose={() => {
          setDeletingGeneration(undefined);
        }}
        onDeleted={(generation, changed) => {
          setDeletingGeneration(undefined);
          setNotice({ kind: 'generation-deleted', shortcode: generation.shortcode });
          onSong(changed);
          reloadGenerations();
          // Its panel closes onto its Version, which stays (frozen) with its other Generations.
          if (generationReference !== undefined && openGeneration?.id === generation.id) {
            closePanel();
          }
        }}
      />
      <MoveToNewSongDialog
        generation={moving}
        song={song}
        others={
          generations.state.phase === 'ready' && moving !== undefined
            ? generations.state.data.filter((generation) => generation.id !== moving.id)
            : []
        }
        onClose={() => {
          setMoving(undefined);
        }}
        onMoved={(result) => {
          setMoving(undefined);
          // The new Song opens with the Generation's panel, which says where it came from.
          void navigate(
            `/songs/${result.song.shortcode}/generations/${result.generation.shortcode}`,
            {
              state: { movedFrom: result.alias },
            },
          );
        }}
      />
    </Stack>
  );
}
