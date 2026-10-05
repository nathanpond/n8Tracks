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
  countSongsWithAnyTag,
  deleteTag,
  mergeTags,
  normaliseTagName,
  readManagedTags,
  TAG_NAME_MAXIMUM_LENGTH,
  tagNameError,
  tagNameKey,
  updateTag,
  useManagedTags,
  type ManagedTag,
  type TagChangeResult,
  type TagEdit,
} from '../api/tags';
import { Notice } from '../components/Notice';
import { TagLabel } from '../songs/SongParts';
import { stateColours } from '../theme/palette';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const CONFLICT_MESSAGE =
  'The Tags were changed somewhere else, so the list has been reloaded. Your change was not applied: check the list and try again.';

/** The twelve palette colours a Tag can have, in palette order. */
const COLOUR_NAMES = Object.keys(stateColours);

/** "Gray", "Red", …: a palette colour's name as a word. */
function colourLabel(colour: string): string {
  return colour.charAt(0).toUpperCase() + colour.slice(1);
}

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

/** The Tags in the table's order: by name (ignoring case) or by Song count, then by name. */
function sortTags(tags: readonly ManagedTag[], sort: Sort): ManagedTag[] {
  const byName = (a: ManagedTag, b: ManagedTag) =>
    tagNameKey(a.name).localeCompare(tagNameKey(b.name));
  return [...tags].sort((a, b) => {
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

/** What the merge confirmation is for: the Tags merged, and the one to preselect as the target. */
interface MergeRequest {
  sources: ManagedTag[];
  target?: string;
}

/**
 * The merge confirmation: choose the Tag to merge into, read how many Songs change (each Song with
 * any of the merged Tags, counted once), and confirm. The Tag merged into keeps its own colour.
 */
function MergeDialog({
  request,
  tags,
  onClose,
  onMerge,
}: {
  request: MergeRequest | undefined;
  tags: ManagedTag[];
  onClose: () => void;
  onMerge: (target: ManagedTag, sources: ManagedTag[]) => Promise<TagChangeResult>;
}) {
  const sources = request?.sources ?? [];
  const candidates = tags.filter((tag) => !sources.some((source) => source.id === tag.id));
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
    void countSongsWithAnyTag(request.sources.map((source) => source.id)).then((total) => {
      if (live) {
        setCount(total ?? 'unknown');
      }
    });
    return () => {
      live = false;
    };
  }, [count, request]);

  const target = candidates.find((tag) => tag.id === targetId);
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
          data={candidates.map((tag) => ({ value: tag.id, label: tag.name }))}
        />
        <Text data-testid="merge-summary">
          {countText} Each Song with {names} gets{' '}
          <strong>{target?.name ?? 'the chosen Tag'}</strong> instead (once), and {names}{' '}
          {sources.length === 1 ? 'is' : 'are'} then deleted.
          {target && ` ${target.name} keeps its colour, ${target.colour}.`}
        </Text>
        {target && (
          <div>
            <TagLabel name={target.name} colour={target.colour} />
          </div>
        )}
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
 * Renames and recolours a Tag; only what changed is sent. A name another Tag already has (in any
 * letter case) is not sent: the dialog offers to merge into that Tag instead.
 */
function EditDialog({
  tag,
  tags,
  onClose,
  onSave,
  onOfferMerge,
}: {
  tag: ManagedTag | undefined;
  tags: ManagedTag[];
  onClose: () => void;
  onSave: (tag: ManagedTag, edit: TagEdit) => Promise<TagChangeResult>;
  onOfferMerge: (source: ManagedTag, target: ManagedTag) => void;
}) {
  const [name, setName] = useState(tag?.name ?? '');
  const [colour, setColour] = useState(tag?.colour ?? '');
  const [error, setError] = useState<string | undefined>();
  const [colourError, setColourError] = useState<string | undefined>();
  const [clash, setClash] = useState<ManagedTag | undefined>();
  const [saving, setSaving] = useState(false);

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (tag === undefined || saving) {
      return;
    }
    const problem = tagNameError(name);
    setError(problem);
    if (problem !== undefined) {
      return;
    }
    const edit: TagEdit = {};
    if (normaliseTagName(name) !== tag.name) {
      edit.name = name;
    }
    if (colour !== tag.colour) {
      edit.colour = colour;
    }
    if (edit.name === undefined && edit.colour === undefined) {
      onClose();
      return;
    }
    if (edit.name !== undefined) {
      const key = tagNameKey(name);
      const holder = tags.find((other) => other.id !== tag.id && tagNameKey(other.name) === key);
      if (holder !== undefined) {
        setClash(holder);
        return;
      }
    }
    setSaving(true);
    const result = await onSave(tag, edit);
    setSaving(false);
    if (result.kind === 'name-taken') {
      const taken = (await readManagedTags())?.find((other) => other.id === result.tagId);
      if (taken === undefined) {
        setError(FAILED_MESSAGE);
      } else {
        setClash(taken);
      }
    } else if (result.kind === 'invalid') {
      setError(result.errors.name?.join(' '));
      setColourError(result.errors.colour?.join(' '));
      if (result.errors.name === undefined && result.errors.colour === undefined) {
        setError(FAILED_MESSAGE);
      }
    } else if (result.kind === 'failed') {
      setError(FAILED_MESSAGE);
    } else {
      onClose();
    }
  };

  return (
    <Modal
      opened={tag !== undefined}
      onClose={onClose}
      title={tag ? `Edit ${tag.name}` : 'Edit Tag'}
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
            description={`Up to ${String(TAG_NAME_MAXIMUM_LENGTH)} characters. Every Song with this Tag shows the new name.`}
            value={name}
            error={error}
            data-autofocus
            onChange={(event) => {
              setName(event.currentTarget.value);
              setError(undefined);
              setClash(undefined);
            }}
          />
          {clash && tag && (
            <Notice title="That name is taken">
              <Text>
                Another Tag is already called <strong>{clash.name}</strong>. Merge {tag.name} into
                it instead, so its Songs have {clash.name}.
              </Text>
              <div>
                <Button
                  size="xs"
                  variant="default"
                  onClick={() => {
                    onOfferMerge(tag, clash);
                  }}
                >
                  Merge into {clash.name}
                </Button>
              </div>
            </Notice>
          )}
          <Radio.Group
            label="Colour"
            value={colour}
            error={colourError}
            onChange={(value) => {
              setColour(value);
              setColourError(undefined);
            }}
          >
            <Group gap="sm" mt={6}>
              {COLOUR_NAMES.map((option) => (
                <Radio
                  key={option}
                  value={option}
                  label={<TagLabel name={colourLabel(option)} colour={option} />}
                />
              ))}
            </Group>
          </Radio.Group>
          <Group justify="end">
            <Button variant="default" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={saving}>
              Save
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}

/**
 * Deleting a Tag that Songs have: it is removed from them. The dialog says how many Songs that is;
 * to give them another Tag instead, the user merges it.
 */
function DeleteDialog({
  target,
  onClose,
  onDelete,
}: {
  target: { tag: ManagedTag; songCount: number } | undefined;
  onClose: () => void;
  onDelete: (tag: ManagedTag) => Promise<TagChangeResult>;
}) {
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const confirm = async () => {
    if (target === undefined) {
      return;
    }
    setDeleting(true);
    const result = await onDelete(target.tag);
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
  const name = target?.tag.name ?? '';
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
          Deleting it removes it from {count === 1 ? 'that Song' : `those ${String(count)} Songs`}.
          To give {count === 1 ? 'it' : 'them'} another Tag instead, cancel and merge {name} into
          that Tag.
        </Text>
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

/** What an edit did, in words: "summer is now orange.", "runing is renamed to running.". */
function editedText(before: ManagedTag, after: ManagedTag | undefined, edit: TagEdit): string {
  const name = after?.name ?? (edit.name === undefined ? before.name : normaliseTagName(edit.name));
  const colour = after?.colour ?? edit.colour ?? before.colour;
  const renamed = edit.name !== undefined;
  const recoloured = edit.colour !== undefined;
  if (renamed && recoloured) {
    return `${before.name} is renamed to ${name} and is now ${colour}.`;
  }
  return renamed ? `${before.name} is renamed to ${name}.` : `${name} is now ${colour}.`;
}

/** The page once the list has loaded: it keeps its own copy, read again after every change. */
function TagsEditor({ initial }: { initial: ManagedTag[] }) {
  const [tags, setTags] = useState(initial);
  const [sort, setSort] = useState<Sort>({ key: 'name', ascending: true });
  const [selected, setSelected] = useState<string[]>([]);
  const [message, setMessage] = useState<Message | undefined>();
  const [editing, setEditing] = useState<ManagedTag | undefined>();
  const [merging, setMerging] = useState<MergeRequest | undefined>();
  const [deleting, setDeleting] = useState<{ tag: ManagedTag; songCount: number }>();
  const busy = useRef(false);

  const refresh = async () => {
    const list = await readManagedTags();
    if (list !== undefined) {
      setTags(list);
      setSelected((ids) => ids.filter((id) => list.some((tag) => tag.id === id)));
    }
  };

  /** Runs one change at a time, then reads the list again; answers what came back. */
  const run = async (
    send: () => Promise<TagChangeResult>,
    done: (result: TagChangeResult) => string,
  ): Promise<TagChangeResult> => {
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

  const remove = async (tag: ManagedTag) => {
    if (tag.songCount > 0) {
      setDeleting({ tag, songCount: tag.songCount });
      return;
    }
    const result = await run(
      () => deleteTag(tag),
      () => `${tag.name} is deleted.`,
    );
    if (result.kind === 'in-use') {
      // Songs were given it since the list was loaded.
      setDeleting({ tag, songCount: result.songCount });
    }
  };

  const rows = sortTags(tags, sort);
  const chosen = tags.filter((tag) => selected.includes(tag.id));
  const toggle = (id: string, on: boolean) => {
    setSelected((ids) => (on ? [...ids, id] : ids.filter((other) => other !== id)));
  };

  return (
    <Stack gap="md">
      <Group gap="sm">
        <Button
          variant="default"
          disabled={chosen.length === 0 || chosen.length === tags.length}
          onClick={() => {
            setMerging({ sources: chosen });
          }}
        >
          Merge into…
        </Button>
        <Text size="sm" c="var(--n8-color-secondary-text)">
          {chosen.length === 0
            ? 'Select the Tags to merge, then choose the Tag to merge them into.'
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

      {tags.length === 0 ? (
        <Text>No Tags yet. Add them to a Song from its Details panel.</Text>
      ) : (
        <Table.ScrollContainer minWidth={480}>
          <Table aria-label="Tags" highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th scope="col" w={40}>
                  <Checkbox
                    aria-label="Select every Tag"
                    checked={chosen.length === tags.length}
                    indeterminate={chosen.length > 0 && chosen.length < tags.length}
                    onChange={(event) => {
                      setSelected(event.currentTarget.checked ? tags.map((tag) => tag.id) : []);
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
                <Table.Th scope="col">Colour</Table.Th>
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
              {rows.map((tag) => (
                <Table.Tr key={tag.id} data-tag-name={tag.name}>
                  <Table.Td>
                    <Checkbox
                      aria-label={`Select ${tag.name}`}
                      checked={selected.includes(tag.id)}
                      onChange={(event) => {
                        toggle(tag.id, event.currentTarget.checked);
                      }}
                    />
                  </Table.Td>
                  <Table.Th scope="row" fw={400}>
                    <TagLabel name={tag.name} colour={tag.colour} />
                  </Table.Th>
                  <Table.Td data-testid="tag-colour">{colourLabel(tag.colour)}</Table.Td>
                  <Table.Td ta="end">{tag.songCount}</Table.Td>
                  <Table.Td>
                    <Group gap={6} justify="end" wrap="nowrap">
                      <Button
                        size="xs"
                        variant="default"
                        aria-label={`Edit ${tag.name}`}
                        onClick={() => {
                          setEditing(tag);
                        }}
                      >
                        Edit
                      </Button>
                      <Button
                        size="xs"
                        variant="default"
                        color="red"
                        aria-label={`Delete ${tag.name}`}
                        onClick={() => {
                          void remove(tag);
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

      <EditDialog
        key={`edit-${editing?.id ?? 'none'}`}
        tag={editing}
        tags={tags}
        onClose={() => {
          setEditing(undefined);
        }}
        onSave={(tag, edit) =>
          run(
            () => updateTag(tag, edit),
            (result) => editedText(tag, result.kind === 'saved' ? result.tag : undefined, edit),
          )
        }
        onOfferMerge={(source, target) => {
          setEditing(undefined);
          setMerging({ sources: [source], target: target.id });
        }}
      />
      <MergeDialog
        key={`merge-${merging ? merging.sources.map((tag) => tag.id).join(',') : 'none'}`}
        request={merging}
        tags={tags}
        onClose={() => {
          setMerging(undefined);
        }}
        onMerge={(target, sources) =>
          run(
            () =>
              mergeTags(
                target,
                sources.map((source) => source.id),
              ),
            () => `${namesText(sources.map((source) => source.name))} merged into ${target.name}.`,
          )
        }
      />
      <DeleteDialog
        key={`delete-${deleting?.tag.id ?? 'none'}`}
        target={deleting}
        onClose={() => {
          setDeleting(undefined);
        }}
        onDelete={(tag) =>
          run(
            () => deleteTag(tag, true),
            () => `${tag.name} is deleted and removed from its Songs.`,
          )
        }
      />
    </Stack>
  );
}

/**
 * Settings → Tags: every Tag in its colour with how many Songs have it, sortable by name and count.
 * The user renames and recolours a Tag (everywhere at once), merges selected Tags into another
 * (which keeps its colour), and deletes one; a Tag in use is removed from its Songs after a
 * confirmation that says how many.
 */
export function TagsPage() {
  const { state, reload } = useManagedTags();

  return (
    <Stack gap="lg">
      <Title order={2}>Tags</Title>
      <Text>
        Your own coloured labels for Songs. Fix, recolour, or merge a Tag here once, and every Song
        with it follows.
      </Text>
      {state.phase === 'loading' && <Loader aria-label="Loading Tags" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="Tags could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {state.phase === 'ready' && <TagsEditor initial={state.data} />}
    </Stack>
  );
}
