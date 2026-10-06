import {
  ActionIcon,
  Anchor,
  Button,
  Fieldset,
  Group,
  Loader,
  Paper,
  Stack,
  Table,
  Text,
  Textarea,
  TextInput,
  Title,
} from '@mantine/core';
import { useCallback, useRef, useState, type SyntheticEvent } from 'react';
import { Link, useLocation, useParams } from 'react-router';
import {
  ARTIST_ALIAS_MAXIMUM_COUNT,
  ARTIST_LINK_MAXIMUM_COUNT,
  ARTIST_NAME_MAXIMUM_LENGTH,
  ARTIST_NOTES_MAXIMUM_LENGTH,
  updateArtist,
  useArtist,
  type Artist,
  type ArtistEdit,
  type ArtistLink,
  type ArtistMatch,
} from '../api/artists';
import type { FieldValue, SaveResult } from '../api/saves';
import { songListParameters, useSongs, type SongQuery } from '../api/songs';
import { useRevisionedSave, type SavedField } from '../common/useRevisionedSave';
import { Notice } from '../components/Notice';
import {
  aliasErrors,
  artistNameError,
  artistNotesError,
  linkErrors,
  normaliseArtistName,
} from './artistRules';
import { DuplicateArtistDialog } from './DuplicateMatches';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

function isFromArtists(value: unknown): value is { artistsSearch: string } {
  return (
    typeof value === 'object' &&
    value !== null &&
    'artistsSearch' in value &&
    typeof value.artistsSearch === 'string'
  );
}

/** Back to the Artists list, in the view this page was opened from when it was opened from one. */
function BackToArtists() {
  const location: { state: unknown } = useLocation();
  const search = isFromArtists(location.state) ? location.state.artistsSearch : '';
  return (
    <Anchor component={Link} to={`/artists${search}`} size="sm">
      ← Artists
    </Anchor>
  );
}

/** What the form holds: the text as typed, before it is normalised for saving. */
interface Drafts {
  name: string;
  aliases: string[];
  notes: string;
  links: { label: string; url: string }[];
}

function draftsOf(artist: Artist): Drafts {
  return {
    name: artist.name,
    aliases: [...artist.aliases],
    notes: artist.notes ?? '',
    links: artist.links.map((link) => ({ label: link.label ?? '', url: link.url })),
  };
}

function normaliseNotes(notes: string): string | null {
  const normalised = notes.replace(/\r\n?/g, '\n').trim();
  return normalised === '' ? null : normalised;
}

function normaliseLinks(links: Drafts['links']): ArtistLink[] {
  return links.map((link) => {
    const label = normaliseArtistName(link.label);
    return { label: label === '' ? null : label, url: link.url.trim() };
  });
}

/** Lists are compared and saved as JSON text, the shared save helper's field values. */
const listValue = (list: readonly unknown[]) => JSON.stringify(list);

function readList<T>(value: FieldValue): T[] {
  return value === null ? [] : (JSON.parse(value) as T[]);
}

const FIELDS: readonly SavedField<Artist>[] = [
  { key: 'name', label: 'Name', read: (artist) => artist.name, show: (value) => value },
  {
    key: 'aliases',
    label: 'Aliases',
    read: (artist) => listValue(artist.aliases),
    show: (value) => readList<string>(value).join(', ') || 'None',
  },
  {
    key: 'notes',
    label: 'Notes',
    read: (artist) => artist.notes,
    show: (value) => value ?? 'None',
  },
  {
    key: 'links',
    label: 'Links',
    read: (artist) => listValue(artist.links),
    show: (value) =>
      readList<ArtistLink>(value)
        .map((link) => (link.label === null ? link.url : `${link.label}: ${link.url}`))
        .join(', ') || 'None',
  },
];

/** The fields of `drafts` (normalised as the API stores them) that differ from `artist`. */
function editOf(drafts: Drafts, artist: Artist): Record<string, FieldValue> {
  const values: Record<string, FieldValue> = {
    name: normaliseArtistName(drafts.name),
    aliases: listValue(drafts.aliases.map(normaliseArtistName)),
    notes: normaliseNotes(drafts.notes),
    links: listValue(normaliseLinks(drafts.links)),
  };
  return Object.fromEntries(
    FIELDS.filter((field) => field.read(artist) !== values[field.key]).map((field) => [
      field.key,
      values[field.key] ?? null,
    ]),
  );
}

/** The edit the API's PATCH takes for what the save helper holds. */
function artistEditOf(edit: Readonly<Record<string, FieldValue>>): ArtistEdit {
  const result: ArtistEdit = {};
  if (Object.hasOwn(edit, 'name')) {
    result.name = edit.name ?? '';
  }
  if (Object.hasOwn(edit, 'aliases')) {
    result.aliases = readList<string>(edit.aliases ?? null);
  }
  if (Object.hasOwn(edit, 'notes')) {
    result.notes = edit.notes ?? null;
  }
  if (Object.hasOwn(edit, 'links')) {
    result.links = readList<ArtistLink>(edit.links ?? null);
  }
  return result;
}

interface FormErrors {
  name?: string;
  aliases: (string | undefined)[];
  aliasList?: string;
  notes?: string;
  links: { label?: string; url?: string }[];
  linkList?: string;
}

const NO_ERRORS: FormErrors = { aliases: [], links: [] };

/** The errors the form shows before anything is sent; undefined when there are none. */
function localErrors(drafts: Drafts): FormErrors | undefined {
  const errors: FormErrors = {
    name: artistNameError(drafts.name),
    aliases: aliasErrors(drafts.aliases, drafts.name),
    notes: artistNotesError(drafts.notes),
    links: drafts.links.map((link) => linkErrors({ label: link.label, url: link.url })),
  };
  const any =
    errors.name !== undefined ||
    errors.notes !== undefined ||
    errors.aliases.some((error) => error !== undefined) ||
    errors.links.some((link) => link.label !== undefined || link.url !== undefined);
  return any ? errors : undefined;
}

/** A row's move and remove controls, named by the row's noun and position. */
function RowControls({
  noun,
  index,
  count,
  onMove,
  onRemove,
}: {
  noun: string;
  index: number;
  count: number;
  onMove?: (from: number, to: number) => void;
  onRemove: (index: number) => void;
}) {
  const position = String(index + 1);
  return (
    <Group gap={4} wrap="nowrap" mt={4}>
      {onMove !== undefined && (
        <>
          <ActionIcon
            variant="default"
            aria-label={`Move ${noun} ${position} up`}
            disabled={index === 0}
            onClick={() => {
              onMove(index, index - 1);
            }}
          >
            <span aria-hidden="true">↑</span>
          </ActionIcon>
          <ActionIcon
            variant="default"
            aria-label={`Move ${noun} ${position} down`}
            disabled={index === count - 1}
            onClick={() => {
              onMove(index, index + 1);
            }}
          >
            <span aria-hidden="true">↓</span>
          </ActionIcon>
        </>
      )}
      <ActionIcon
        variant="default"
        aria-label={`Remove ${noun} ${position}`}
        onClick={() => {
          onRemove(index);
        }}
      >
        <span aria-hidden="true">×</span>
      </ActionIcon>
    </Group>
  );
}

function move<T>(list: readonly T[], from: number, to: number): T[] {
  const next = [...list];
  const [item] = next.splice(from, 1);
  if (item !== undefined) {
    next.splice(to, 0, item);
  }
  return next;
}

/** A section the credit or Album stories fill; until then it says nothing is credited. */
function CreditedSection({ title, count, noun }: { title: string; count: number; noun: string }) {
  const id = `artist-${title.toLowerCase()}`;
  return (
    <section aria-labelledby={id}>
      <Stack gap="xs">
        <Title order={3} id={id}>
          {title}
        </Title>
        <Paper p="sm" withBorder>
          <Text size="sm">
            {count === 0
              ? `No ${noun} are credited to this Artist yet.`
              : `${String(count)} ${count === 1 ? noun.replace(/s$/, '') : noun} credited to this Artist.`}
          </Text>
        </Paper>
      </Stack>
    </section>
  );
}

/**
 * The Songs crediting the Artist, by title, each with its role (primary or featured), the first
 * fifty here and every one in the Songs table filtered by the Artist.
 */
function CreditedSongs({ artist }: { artist: Artist }) {
  const query: SongQuery = {
    sort: 'title',
    direction: 'asc',
    states: [],
    genres: [],
    tags: [],
    artists: [artist.id],
    page: 1,
  };
  const { state, reload } = useSongs(query);
  const page = state.phase === 'ready' ? state.data : undefined;
  return (
    <section aria-labelledby="artist-songs">
      <Stack gap="xs">
        <Title order={3} id="artist-songs">
          Songs
        </Title>
        {state.phase === 'loading' && <Loader size="sm" aria-label="Loading the Songs" />}
        {(state.phase === 'error' || state.phase === 'not-found') && (
          <Group gap="xs">
            <Text size="sm" c="var(--mantine-color-error)">
              The Songs could not be loaded.
            </Text>
            <Button variant="default" size="compact-xs" onClick={reload}>
              Try again
            </Button>
          </Group>
        )}
        {page?.total === 0 && (
          <Paper p="sm" withBorder>
            <Text size="sm">No Songs are credited to this Artist yet.</Text>
          </Paper>
        )}
        {page !== undefined && page.total > 0 && (
          <>
            <Table withTableBorder aria-label={`Songs credited to ${artist.name}`}>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">Song</Table.Th>
                  <Table.Th scope="col">Shortcode</Table.Th>
                  <Table.Th scope="col">Role</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {page.items.map((song) => (
                  <Table.Tr key={song.id} data-song={song.shortcode}>
                    <Table.Td>
                      <Anchor component={Link} to={`/songs/${song.shortcode}`}>
                        {song.title}
                      </Anchor>
                    </Table.Td>
                    <Table.Td>{song.shortcode}</Table.Td>
                    <Table.Td>
                      {song.credits.primary?.id === artist.id ? 'Primary' : 'Featured'}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
            {page.total > page.items.length && (
              <Anchor
                component={Link}
                to={`/songs?${songListParameters(query).toString()}`}
                size="sm"
              >
                Show all {page.total} Songs in the Songs table
              </Anchor>
            )}
          </>
        )}
      </Stack>
    </section>
  );
}

/** The Artist's page once it has loaded: the form, saved through the shared save helper, and its sections. */
function LoadedArtist({ initial }: { initial: Artist }) {
  const [artist, setArtist] = useState(initial);
  const [drafts, setDrafts] = useState(() => draftsOf(initial));
  const [errors, setErrors] = useState<FormErrors>(NO_ERRORS);
  const [status, setStatus] = useState<'idle' | 'saving' | 'saved' | 'failed'>('idle');
  const [duplicate, setDuplicate] = useState<{
    matches: ArtistMatch[];
    resolve: (confirmed: boolean) => void;
  } | null>(null);
  const declined = useRef(false);
  const latest = useRef(initial);
  const takeRecord = useCallback((next: Artist) => {
    latest.current = next;
    setArtist(next);
  }, []);

  const send = useCallback(
    async (
      base: Artist,
      edit: Readonly<Record<string, FieldValue>>,
    ): Promise<SaveResult<Artist>> => {
      declined.current = false;
      const body = artistEditOf(edit);
      const first = await updateArtist(base, body);
      if (first.kind !== 'duplicate') {
        return first;
      }
      const confirmed = await new Promise<boolean>((resolve) => {
        setDuplicate({ matches: first.matches, resolve });
      });
      if (!confirmed) {
        declined.current = true;
        return { kind: 'failed', reason: 'refused' };
      }
      const second = await updateArtist(base, body, true);
      return second.kind === 'duplicate' ? { kind: 'failed', reason: 'refused' } : second;
    },
    [],
  );

  const { saveFields, dialog } = useRevisionedSave({
    record: artist,
    onRecord: takeRecord,
    fields: FIELDS,
    send,
    subject: 'This Artist',
  });

  const edit = editOf(drafts, artist);
  const dirty = Object.keys(edit).length > 0;

  const change = (next: Partial<Drafts>) => {
    setDrafts((previous) => ({ ...previous, ...next }));
    setStatus('idle');
  };

  const save = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    const local = localErrors(drafts);
    if (local !== undefined) {
      setErrors(local);
      return;
    }
    setErrors(NO_ERRORS);
    setStatus('saving');
    const outcome = await saveFields(edit);
    switch (outcome.kind) {
      case 'saved':
      case 'reloaded':
        setDrafts(draftsOf(latest.current));
        setStatus(outcome.kind === 'saved' ? 'saved' : 'idle');
        return;
      case 'keep-editing':
        setStatus('idle');
        return;
      case 'invalid':
        setErrors({
          name: outcome.errors.name?.join(' '),
          aliases: [],
          aliasList: outcome.errors.aliases?.join(' '),
          notes: outcome.errors.notes?.join(' '),
          links: [],
          linkList: outcome.errors.links?.join(' '),
        });
        setStatus('idle');
        return;
      case 'failed':
        setStatus(declined.current ? 'idle' : 'failed');
    }
  };

  const discard = () => {
    setDrafts(draftsOf(artist));
    setErrors(NO_ERRORS);
    setStatus('idle');
  };

  return (
    <Stack gap="lg">
      <BackToArtists />
      <Title order={2}>{artist.name}</Title>

      <form
        noValidate
        aria-label="Artist details"
        onSubmit={(event) => {
          void save(event);
        }}
      >
        <Stack gap="md">
          <TextInput
            label="Name"
            description={`The name shown for the Artist, up to ${String(ARTIST_NAME_MAXIMUM_LENGTH)} characters. Other Artists may share it.`}
            required
            value={drafts.name}
            onChange={(event) => {
              change({ name: event.currentTarget.value });
            }}
            error={errors.name}
            aria-invalid={errors.name !== undefined}
          />

          <Fieldset legend="Aliases">
            <Stack gap="xs">
              {drafts.aliases.length === 0 && (
                <Text size="sm" c="var(--n8-color-secondary-text)">
                  No aliases. Add other names the Artist goes by; search finds them too.
                </Text>
              )}
              {drafts.aliases.map((alias, index) => (
                <Group key={index} align="flex-start" wrap="nowrap" gap="xs">
                  <TextInput
                    label={`Alias ${String(index + 1)}`}
                    value={alias}
                    onChange={(event) => {
                      const value = event.currentTarget.value;
                      change({
                        aliases: drafts.aliases.map((item, i) => (i === index ? value : item)),
                      });
                    }}
                    error={errors.aliases[index]}
                    aria-invalid={errors.aliases[index] !== undefined}
                    style={{ flex: 1 }}
                  />
                  <Stack gap={0} pt={22}>
                    <RowControls
                      noun="alias"
                      index={index}
                      count={drafts.aliases.length}
                      onRemove={(i) => {
                        change({ aliases: drafts.aliases.filter((_, j) => j !== i) });
                      }}
                    />
                  </Stack>
                </Group>
              ))}
              {errors.aliasList !== undefined && (
                <Text size="sm" c="var(--mantine-color-error)">
                  {errors.aliasList}
                </Text>
              )}
              <Group>
                <Button
                  variant="default"
                  size="xs"
                  disabled={drafts.aliases.length >= ARTIST_ALIAS_MAXIMUM_COUNT}
                  onClick={() => {
                    change({ aliases: [...drafts.aliases, ''] });
                  }}
                >
                  Add alias
                </Button>
              </Group>
            </Stack>
          </Fieldset>

          <Textarea
            label="Notes"
            description={`Plain text, up to ${ARTIST_NOTES_MAXIMUM_LENGTH.toLocaleString('en-US')} characters.`}
            rows={4}
            resize="vertical"
            value={drafts.notes}
            onChange={(event) => {
              change({ notes: event.currentTarget.value });
            }}
            error={errors.notes}
            aria-invalid={errors.notes !== undefined}
          />

          <Fieldset legend="Links">
            <Stack gap="xs">
              {drafts.links.length === 0 && (
                <Text size="sm" c="var(--n8-color-secondary-text)">
                  No links. Add the Artist&apos;s pages elsewhere, each with an optional label.
                </Text>
              )}
              {drafts.links.map((link, index) => {
                const position = String(index + 1);
                const update = (next: Partial<Drafts['links'][number]>) => {
                  change({
                    links: drafts.links.map((item, i) =>
                      i === index ? { ...item, ...next } : item,
                    ),
                  });
                };
                return (
                  <Group key={index} align="flex-start" wrap="wrap" gap="xs">
                    <TextInput
                      label={`Link ${position} label`}
                      value={link.label}
                      onChange={(event) => {
                        update({ label: event.currentTarget.value });
                      }}
                      error={errors.links[index]?.label}
                      aria-invalid={errors.links[index]?.label !== undefined}
                      w={180}
                    />
                    <TextInput
                      label={`Link ${position} URL`}
                      type="url"
                      placeholder="https://"
                      value={link.url}
                      onChange={(event) => {
                        update({ url: event.currentTarget.value });
                      }}
                      error={errors.links[index]?.url}
                      aria-invalid={errors.links[index]?.url !== undefined}
                      style={{ flex: 1, minWidth: 220 }}
                    />
                    <Stack gap={0} pt={22}>
                      <RowControls
                        noun="link"
                        index={index}
                        count={drafts.links.length}
                        onMove={(from, to) => {
                          change({ links: move(drafts.links, from, to) });
                        }}
                        onRemove={(i) => {
                          change({ links: drafts.links.filter((_, j) => j !== i) });
                        }}
                      />
                    </Stack>
                  </Group>
                );
              })}
              {errors.linkList !== undefined && (
                <Text size="sm" c="var(--mantine-color-error)">
                  {errors.linkList}
                </Text>
              )}
              {artist.links.length > 0 && (
                <Stack gap={2}>
                  <Text size="sm" fw={500}>
                    Saved links
                  </Text>
                  {artist.links.map((link, index) => (
                    <Anchor
                      key={index}
                      href={link.url}
                      target="_blank"
                      rel="noopener noreferrer"
                      size="sm"
                      underline="always"
                    >
                      {link.label ?? link.url}
                    </Anchor>
                  ))}
                </Stack>
              )}
              <Group>
                <Button
                  variant="default"
                  size="xs"
                  disabled={drafts.links.length >= ARTIST_LINK_MAXIMUM_COUNT}
                  onClick={() => {
                    change({ links: [...drafts.links, { label: '', url: '' }] });
                  }}
                >
                  Add link
                </Button>
              </Group>
            </Stack>
          </Fieldset>

          <div role="status">
            {status === 'saved' && !dirty && <Text size="sm">Saved.</Text>}
            {status === 'failed' && (
              <Notice title="Artist not saved">
                <Text>{FAILED_MESSAGE}</Text>
              </Notice>
            )}
          </div>
          <Group>
            <Button type="submit" disabled={!dirty} loading={status === 'saving'}>
              Save
            </Button>
            <Button variant="default" disabled={!dirty} onClick={discard}>
              Discard changes
            </Button>
          </Group>
        </Stack>
      </form>

      <CreditedSongs artist={artist} />
      <CreditedSection title="Albums" count={artist.albumCount} noun="Albums" />

      <DuplicateArtistDialog
        matches={duplicate?.matches ?? null}
        onConfirm={() => {
          duplicate?.resolve(true);
          setDuplicate(null);
        }}
        onCancel={() => {
          duplicate?.resolve(false);
          setDuplicate(null);
        }}
      />
      {dialog}
    </Stack>
  );
}

/**
 * An Artist's page (`/artists/<id>`): its name, aliases, notes, and links, edited in one form and
 * saved under the Artist's revision; a new name or alias another Artist has asks for confirmation.
 * Below are the Songs credited to it, with the role, and the Albums, which the Album story fills.
 */
export function ArtistPage() {
  const { id = '' } = useParams();
  const { state, reload } = useArtist(id);

  if (state.phase === 'loading') {
    return <Loader aria-label="Loading the Artist" />;
  }
  if (state.phase === 'not-found') {
    return (
      <Stack gap="md">
        <BackToArtists />
        <Title order={2}>No such Artist</Title>
        <Text>There is no Artist at this address. It may have been typed wrongly.</Text>
      </Stack>
    );
  }
  if (state.phase === 'error') {
    return (
      <Stack gap="md">
        <BackToArtists />
        <Notice title="The Artist could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <Group>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </Group>
        </Notice>
      </Stack>
    );
  }
  return <LoadedArtist key={state.data.id} initial={state.data} />;
}
