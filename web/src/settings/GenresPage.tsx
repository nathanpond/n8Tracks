import {
  Button,
  Checkbox,
  Group,
  Loader,
  Modal,
  NativeSelect,
  Radio,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
  UnstyledButton,
  VisuallyHidden,
} from '@mantine/core';
import { useEffect, useRef, useState, type SyntheticEvent } from 'react';
import {
  countSongsWithAny,
  deleteGenre,
  GENRE_NAME_MAXIMUM_LENGTH,
  genreNameError,
  genreNameKey,
  mergeGenres,
  normaliseGenreName,
  readManagedGenres,
  renameGenre,
  useManagedGenres,
  type GenreChangeResult,
  type GenreDeleteChoice,
  type ManagedGenre,
} from '../api/genres';
import { Notice } from '../components/Notice';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const CONFLICT_MESSAGE =
  'The Genres were changed somewhere else, so the list has been reloaded. Your change was not applied: check the list and try again.';

/** "1 Song", "3 Songs", "No Songs". */
function songCountText(count: number): string {
  if (count === 0) {
    return 'No Songs';
  }
  return count === 1 ? '1 Song' : `${String(count)} Songs`;
}

/** A list of names as running text: "A", "A and B", "A, B, and C". */
function namesText(names: readonly string[]): string {
  if (names.length <= 2) {
    return names.join(' and ');
  }
  return `${names.slice(0, -1).join(', ')}, and ${names.at(-1) ?? ''}`;
}

type SortKey = 'name' | 'count';
interface Sort {
  key: SortKey;
  ascending: boolean;
}

/** The Genres in the table's order: by name (ignoring case) or by Song count, then by name. */
function sortGenres(genres: readonly ManagedGenre[], sort: Sort): ManagedGenre[] {
  const byName = (a: ManagedGenre, b: ManagedGenre) =>
    genreNameKey(a.name).localeCompare(genreNameKey(b.name));
  return [...genres].sort((a, b) => {
    const order = sort.key === 'name' ? byName(a, b) : a.songCount - b.songCount || byName(a, b);
    return sort.ascending ? order : -order;
  });
}

/** A column header that sorts by it: once in its starting direction, again reversed. */
function SortHeader({
  label,
  sortKey,
  sort,
  onSort,
  align,
}: {
  label: string;
  sortKey: SortKey;
  sort: Sort;
  onSort: (key: SortKey) => void;
  align?: 'end';
}) {
  const active = sort.key === sortKey;
  return (
    <Table.Th
      scope="col"
      ta={align}
      aria-sort={active ? (sort.ascending ? 'ascending' : 'descending') : undefined}
    >
      <UnstyledButton
        fw={700}
        fz="sm"
        onClick={() => {
          onSort(sortKey);
        }}
      >
        {label}
        <span aria-hidden="true">{active ? (sort.ascending ? ' ▲' : ' ▼') : ''}</span>
      </UnstyledButton>
    </Table.Th>
  );
}

/** What the merge confirmation is for: the Genres merged, and the one to preselect as the target. */
interface MergeRequest {
  sources: ManagedGenre[];
  target?: string;
}

/**
 * The merge confirmation: choose the Genre to merge into, read how many Songs change (each Song
 * with any of the merged Genres, counted once), and confirm.
 */
function MergeDialog({
  request,
  genres,
  onClose,
  onMerge,
}: {
  request: MergeRequest | undefined;
  genres: ManagedGenre[];
  onClose: () => void;
  onMerge: (target: ManagedGenre, sources: ManagedGenre[]) => Promise<GenreChangeResult>;
}) {
  const sources = request?.sources ?? [];
  const candidates = genres.filter((genre) => !sources.some((source) => source.id === genre.id));
  const [targetId, setTargetId] = useState(request?.target ?? candidates[0]?.id ?? '');
  const [count, setCount] = useState<number | 'counting' | 'unknown'>(() => {
    if (request === undefined) {
      return 'unknown';
    }
    return sources.length === 1 ? (sources[0]?.songCount ?? 0) : 'counting';
  });
  const [merging, setMerging] = useState(false);
  const [error, setError] = useState<string | undefined>();

  useEffect(() => {
    if (count !== 'counting' || request === undefined) {
      return;
    }
    let live = true;
    void countSongsWithAny(request.sources.map((source) => source.id)).then((total) => {
      if (live) {
        setCount(total ?? 'unknown');
      }
    });
    return () => {
      live = false;
    };
  }, [count, request]);

  const target = candidates.find((genre) => genre.id === targetId);
  const names = namesText(sources.map((source) => source.name));

  const confirm = async () => {
    if (target === undefined) {
      return;
    }
    setMerging(true);
    const result = await onMerge(target, sources);
    setMerging(false);
    if (result.kind === 'invalid') {
      setError(Object.values(result.errors).flat().join(' ') || FAILED_MESSAGE);
    } else if (result.kind === 'failed') {
      setError(FAILED_MESSAGE);
    } else {
      onClose();
    }
  };

  let countText: string;
  if (count === 'counting') {
    countText = 'Counting the Songs that change…';
  } else if (count === 'unknown') {
    countText = `Up to ${songCountText(sources.reduce((sum, source) => sum + source.songCount, 0))} change.`;
  } else {
    countText = `${songCountText(count)} ${count === 1 ? 'changes' : 'change'}.`;
  }

  return (
    <Modal
      opened={request !== undefined}
      onClose={onClose}
      title={`Merge ${names}`}
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <NativeSelect
          label="Merge into"
          value={targetId}
          error={error}
          onChange={(event) => {
            setTargetId(event.currentTarget.value);
            setError(undefined);
          }}
          data={candidates.map((genre) => ({ value: genre.id, label: genre.name }))}
        />
        <Text data-testid="merge-summary">
          {countText} Each Song with {names} gets{' '}
          <strong>{target?.name ?? 'the chosen Genre'}</strong> instead (once), and {names}{' '}
          {sources.length === 1 ? 'is' : 'are'} then deleted.
        </Text>
        <Group justify="end">
          <Button variant="default" onClick={onClose} data-autofocus>
            Cancel
          </Button>
          <Button
            loading={merging}
            disabled={target === undefined || count === 'counting'}
            onClick={() => {
              void confirm();
            }}
          >
            Merge
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/**
 * Renames a Genre. A name another Genre already has (in any letter case) is not sent as a rename:
 * the dialog offers to merge into that Genre instead.
 */
function RenameDialog({
  genre,
  genres,
  onClose,
  onRename,
  onOfferMerge,
}: {
  genre: ManagedGenre | undefined;
  genres: ManagedGenre[];
  onClose: () => void;
  onRename: (genre: ManagedGenre, name: string) => Promise<GenreChangeResult>;
  onOfferMerge: (source: ManagedGenre, target: ManagedGenre) => void;
}) {
  const [name, setName] = useState(genre?.name ?? '');
  const [error, setError] = useState<string | undefined>();
  const [clash, setClash] = useState<ManagedGenre | undefined>();
  const [saving, setSaving] = useState(false);

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (genre === undefined || saving) {
      return;
    }
    const problem = genreNameError(name);
    setError(problem);
    if (problem !== undefined) {
      return;
    }
    if (normaliseGenreName(name) === genre.name) {
      onClose();
      return;
    }
    const key = genreNameKey(name);
    const holder = genres.find(
      (other) => other.id !== genre.id && genreNameKey(other.name) === key,
    );
    if (holder !== undefined) {
      setClash(holder);
      return;
    }
    setSaving(true);
    const result = await onRename(genre, name);
    setSaving(false);
    if (result.kind === 'name-taken') {
      const taken = (await readManagedGenres())?.find((other) => other.id === result.genreId);
      if (taken === undefined) {
        setError(FAILED_MESSAGE);
      } else {
        setClash(taken);
      }
    } else if (result.kind === 'invalid') {
      setError(result.errors.name?.join(' ') ?? FAILED_MESSAGE);
    } else if (result.kind === 'failed') {
      setError(FAILED_MESSAGE);
    } else {
      onClose();
    }
  };

  return (
    <Modal
      opened={genre !== undefined}
      onClose={onClose}
      title={genre ? `Rename ${genre.name}` : 'Rename Genre'}
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <form
        noValidate
        onSubmit={(event) => {
          void submit(event);
        }}
      >
        <Stack gap="md">
          <TextInput
            label="Name"
            description={`Up to ${String(GENRE_NAME_MAXIMUM_LENGTH)} characters. Every Song with this Genre shows the new name.`}
            value={name}
            error={error}
            data-autofocus
            onChange={(event) => {
              setName(event.currentTarget.value);
              setError(undefined);
              setClash(undefined);
            }}
          />
          {clash && genre && (
            <Notice title="That name is taken">
              <Text>
                Another Genre is already called <strong>{clash.name}</strong>. Merge {genre.name}{' '}
                into it instead, so its Songs have {clash.name}.
              </Text>
              <div>
                <Button
                  size="xs"
                  variant="default"
                  onClick={() => {
                    onOfferMerge(genre, clash);
                  }}
                >
                  Merge into {clash.name}
                </Button>
              </div>
            </Notice>
          )}
          <Group justify="end">
            <Button variant="default" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={saving}>
              Rename
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}

/**
 * Deleting a Genre that Songs have: remove it from them, or give them another Genre instead. The
 * dialog says how many Songs that is.
 */
function DeleteDialog({
  target,
  genres,
  onClose,
  onDelete,
}: {
  target: { genre: ManagedGenre; songCount: number } | undefined;
  genres: ManagedGenre[];
  onClose: () => void;
  onDelete: (genre: ManagedGenre, choice: GenreDeleteChoice) => Promise<GenreChangeResult>;
}) {
  const others = genres.filter((genre) => genre.id !== target?.genre.id);
  const [mode, setMode] = useState<'remove' | 'reassign'>('remove');
  const [reassignTo, setReassignTo] = useState(others[0]?.id ?? '');
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const confirm = async () => {
    if (target === undefined) {
      return;
    }
    setDeleting(true);
    const result = await onDelete(
      target.genre,
      mode === 'remove' ? { removeFromSongs: true } : { reassignTo },
    );
    setDeleting(false);
    if (result.kind === 'invalid') {
      setError(Object.values(result.errors).flat().join(' ') || FAILED_MESSAGE);
    } else if (result.kind === 'failed') {
      setError(FAILED_MESSAGE);
    } else {
      onClose();
    }
  };

  const count = target?.songCount ?? 0;
  const name = target?.genre.name ?? '';
  return (
    <Modal
      opened={target !== undefined}
      onClose={onClose}
      title={`Delete ${name}`}
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text data-testid="delete-summary">
          {count === 1 ? '1 Song has' : `${String(count)} Songs have`} <strong>{name}</strong>.
          Choose what happens to {count === 1 ? 'it' : 'them'}; the Genre is then deleted.
        </Text>
        <Radio.Group
          label="Its Songs"
          value={mode}
          onChange={(value) => {
            setMode(value === 'reassign' ? 'reassign' : 'remove');
            setError(undefined);
          }}
        >
          <Stack gap="xs" mt={6}>
            <Radio value="remove" label={`Remove ${name} from them`} />
            <Radio
              value="reassign"
              label="Reassign them to another Genre"
              disabled={others.length === 0}
            />
          </Stack>
        </Radio.Group>
        {mode === 'reassign' && (
          <NativeSelect
            label="Reassign to"
            value={reassignTo}
            onChange={(event) => {
              setReassignTo(event.currentTarget.value);
              setError(undefined);
            }}
            data={others.map((genre) => ({ value: genre.id, label: genre.name }))}
          />
        )}
        {error && (
          <Text c="var(--mantine-color-error)" size="sm">
            {error}
          </Text>
        )}
        <Group justify="end">
          <Button variant="default" onClick={onClose} data-autofocus>
            Cancel
          </Button>
          <Button
            color="red"
            loading={deleting}
            onClick={() => {
              void confirm();
            }}
          >
            Delete {name}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** What the page tells the user after a change. */
interface Message {
  text: string;
  tone: 'info' | 'problem';
}

/** The page once the list has loaded: it keeps its own copy, read again after every change. */
function GenresEditor({ initial }: { initial: ManagedGenre[] }) {
  const [genres, setGenres] = useState(initial);
  const [sort, setSort] = useState<Sort>({ key: 'name', ascending: true });
  const [selected, setSelected] = useState<string[]>([]);
  const [message, setMessage] = useState<Message | undefined>();
  const [renaming, setRenaming] = useState<ManagedGenre | undefined>();
  const [merging, setMerging] = useState<MergeRequest | undefined>();
  const [deleting, setDeleting] = useState<{ genre: ManagedGenre; songCount: number }>();
  const busy = useRef(false);

  const refresh = async () => {
    const list = await readManagedGenres();
    if (list !== undefined) {
      setGenres(list);
      setSelected((ids) => ids.filter((id) => list.some((genre) => genre.id === id)));
    }
  };

  /** Runs one change at a time, then reads the list again; answers what came back. */
  const run = async (
    send: () => Promise<GenreChangeResult>,
    done: (result: GenreChangeResult) => string,
  ): Promise<GenreChangeResult> => {
    if (busy.current) {
      return { kind: 'failed' };
    }
    busy.current = true;
    const result = await send();
    busy.current = false;
    switch (result.kind) {
      case 'saved':
      case 'deleted':
        setMessage({ text: done(result), tone: 'info' });
        await refresh();
        break;
      case 'conflict':
      case 'not-found':
        setMessage({ text: CONFLICT_MESSAGE, tone: 'problem' });
        await refresh();
        break;
      case 'failed':
        setMessage({ text: FAILED_MESSAGE, tone: 'problem' });
        break;
      default:
        // `invalid`, `name-taken`, and `in-use` are shown where the change was asked for.
        break;
    }
    return result;
  };

  const remove = async (genre: ManagedGenre) => {
    if (genre.songCount > 0) {
      setDeleting({ genre, songCount: genre.songCount });
      return;
    }
    const result = await run(
      () => deleteGenre(genre),
      () => `${genre.name} is deleted.`,
    );
    if (result.kind === 'in-use') {
      // Songs were given it since the list was loaded.
      setDeleting({ genre, songCount: result.songCount });
    }
  };

  const rows = sortGenres(genres, sort);
  const chosen = genres.filter((genre) => selected.includes(genre.id));
  const toggle = (id: string, on: boolean) => {
    setSelected((ids) => (on ? [...ids, id] : ids.filter((other) => other !== id)));
  };

  return (
    <Stack gap="md">
      <Group gap="sm">
        <Button
          variant="default"
          disabled={chosen.length === 0 || chosen.length === genres.length}
          onClick={() => {
            setMerging({ sources: chosen });
          }}
        >
          Merge into…
        </Button>
        <Text size="sm" c="var(--n8-color-secondary-text)">
          {chosen.length === 0
            ? 'Select the Genres to merge, then choose the Genre to merge them into.'
            : `${String(chosen.length)} selected.`}
        </Text>
      </Group>

      <Group gap="xs" role="status" aria-live="polite" mih={28}>
        {message &&
          (message.tone === 'problem' ? (
            <Notice title="Not changed">
              <Text>{message.text}</Text>
            </Notice>
          ) : (
            <Text>{message.text}</Text>
          ))}
      </Group>

      {genres.length === 0 ? (
        <Text>No Genres yet. Add them to a Song from its Details panel.</Text>
      ) : (
        <Table.ScrollContainer minWidth={420}>
          <Table aria-label="Genres" highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th scope="col" w={40}>
                  <Checkbox
                    aria-label="Select every Genre"
                    checked={chosen.length === genres.length}
                    indeterminate={chosen.length > 0 && chosen.length < genres.length}
                    onChange={(event) => {
                      setSelected(
                        event.currentTarget.checked ? genres.map((genre) => genre.id) : [],
                      );
                    }}
                  />
                </Table.Th>
                <SortHeader
                  label="Name"
                  sortKey="name"
                  sort={sort}
                  onSort={(key) => {
                    setSort((now) =>
                      now.key === key
                        ? { key, ascending: !now.ascending }
                        : { key, ascending: true },
                    );
                  }}
                />
                <SortHeader
                  label="Songs"
                  sortKey="count"
                  sort={sort}
                  align="end"
                  onSort={(key) => {
                    setSort((now) =>
                      now.key === key
                        ? { key, ascending: !now.ascending }
                        : { key, ascending: false },
                    );
                  }}
                />
                <Table.Th scope="col">
                  <VisuallyHidden>Actions</VisuallyHidden>
                </Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((genre) => (
                <Table.Tr key={genre.id} data-genre-name={genre.name}>
                  <Table.Td>
                    <Checkbox
                      aria-label={`Select ${genre.name}`}
                      checked={selected.includes(genre.id)}
                      onChange={(event) => {
                        toggle(genre.id, event.currentTarget.checked);
                      }}
                    />
                  </Table.Td>
                  <Table.Th scope="row" fw={400}>
                    {genre.name}
                  </Table.Th>
                  <Table.Td ta="end">{genre.songCount}</Table.Td>
                  <Table.Td>
                    <Group gap={6} justify="end" wrap="nowrap">
                      <Button
                        size="xs"
                        variant="default"
                        aria-label={`Rename ${genre.name}`}
                        onClick={() => {
                          setRenaming(genre);
                        }}
                      >
                        Rename
                      </Button>
                      <Button
                        size="xs"
                        variant="default"
                        color="red"
                        aria-label={`Delete ${genre.name}`}
                        onClick={() => {
                          void remove(genre);
                        }}
                      >
                        Delete
                      </Button>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      <RenameDialog
        key={`rename-${renaming?.id ?? 'none'}`}
        genre={renaming}
        genres={genres}
        onClose={() => {
          setRenaming(undefined);
        }}
        onRename={(genre, name) =>
          run(
            () => renameGenre(genre, name),
            (result) =>
              `${genre.name} is renamed to ${result.kind === 'saved' ? result.genre.name : normaliseGenreName(name)}.`,
          )
        }
        onOfferMerge={(source, target) => {
          setRenaming(undefined);
          setMerging({ sources: [source], target: target.id });
        }}
      />
      <MergeDialog
        key={`merge-${merging ? merging.sources.map((genre) => genre.id).join(',') : 'none'}`}
        request={merging}
        genres={genres}
        onClose={() => {
          setMerging(undefined);
        }}
        onMerge={(target, sources) =>
          run(
            () =>
              mergeGenres(
                target,
                sources.map((source) => source.id),
              ),
            () => `${namesText(sources.map((source) => source.name))} merged into ${target.name}.`,
          )
        }
      />
      <DeleteDialog
        key={`delete-${deleting?.genre.id ?? 'none'}`}
        target={deleting}
        genres={genres}
        onClose={() => {
          setDeleting(undefined);
        }}
        onDelete={(genre, choice) =>
          run(
            () => deleteGenre(genre, choice),
            () => {
              if ('removeFromSongs' in choice) {
                return `${genre.name} is deleted and removed from its Songs.`;
              }
              const to = genres.find((other) => other.id === choice.reassignTo);
              return `${genre.name} is deleted; its Songs now have ${to?.name ?? 'the Genre chosen'}.`;
            },
          )
        }
      />
    </Stack>
  );
}

/**
 * Settings → Genres: every Genre with how many Songs have it, sortable by name and count. The user
 * renames a Genre (everywhere at once), merges selected Genres into another, and deletes one,
 * choosing for a Genre in use whether its Songs lose it or get another instead. Each change that
 * moves Songs first says how many.
 */
export function GenresPage() {
  const { state, reload } = useManagedGenres();

  return (
    <Stack gap="lg">
      <Title order={2}>Genres</Title>
      <Text>
        Your own list of Genres. Fix a misspelt or duplicate Genre here once, and every Song with it
        follows.
      </Text>
      {state.phase === 'loading' && <Loader aria-label="Loading Genres" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="Genres could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {state.phase === 'ready' && <GenresEditor initial={state.data} />}
    </Stack>
  );
}
