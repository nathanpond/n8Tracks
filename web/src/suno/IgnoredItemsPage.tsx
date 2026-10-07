import {
  Button,
  Checkbox,
  Group,
  Loader,
  Modal,
  NativeSelect,
  Pagination,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useState } from 'react';
import { useSearchParams } from 'react-router';
import {
  IGNORED_STATUSES,
  ignoredParameters,
  ignoredQueryFrom,
  removeIgnoredItems,
  useIgnoredItems,
  type IgnoredItem,
  type IgnoredPage,
  type IgnoredQuery,
  type IgnoredStatus,
  type IgnoredWorkspace,
} from '../api/sunoIgnored';
import { formatDate, useConfiguredTimeZone } from '../api/timeZone';
import { Notice } from '../components/Notice';
import { itemCountText, STATUS_LABELS, statusText } from './ignoredItemsRules';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const PAGE_CONTROL_LABELS: Record<'first' | 'previous' | 'next' | 'last', string> = {
  first: 'First page',
  previous: 'Previous page',
  next: 'Next page',
  last: 'Last page',
};

/** A workspace as the list and its filter name it: its name, else its Suno ID. */
function workspaceText(workspace: { id: string; name: string | null }): string {
  return workspace.name === null || workspace.name.trim() === '' ? workspace.id : workspace.name;
}

/** The search and the two filters, kept in the address so a reload or a shared link shows the same list. */
function Filters({
  query,
  workspaces,
  onChange,
}: {
  query: IgnoredQuery;
  workspaces: IgnoredWorkspace[];
  onChange: (query: IgnoredQuery) => void;
}) {
  const [search, setSearch] = useState(query.q);

  return (
    <Group
      component="form"
      role="search"
      aria-label="Ignored items"
      align="flex-end"
      gap="sm"
      onSubmit={(event) => {
        event.preventDefault();
        onChange({ ...query, q: search, page: 1 });
      }}
    >
      <TextInput
        label="Search"
        description="A title, or the start of a Suno ID"
        value={search}
        onChange={(event) => {
          setSearch(event.currentTarget.value);
        }}
      />
      <Button type="submit" variant="default">
        Search
      </Button>
      <NativeSelect
        label="Workspace"
        data={[
          { value: '', label: 'All workspaces' },
          ...workspaces.map((workspace) => ({
            value: workspace.id,
            label: workspaceText(workspace),
          })),
        ]}
        value={query.workspace}
        onChange={(event) => {
          onChange({ ...query, workspace: event.currentTarget.value, page: 1 });
        }}
      />
      <NativeSelect
        label="Suno status"
        data={[
          { value: '', label: 'Any status' },
          ...IGNORED_STATUSES.map((status) => ({ value: status, label: STATUS_LABELS[status] })),
        ]}
        value={query.status}
        onChange={(event) => {
          const status = event.currentTarget.value as IgnoredStatus | '';
          onChange({ ...query, status, page: 1 });
        }}
      />
    </Group>
  );
}

/** The confirmation of a removal: how many, and that nothing is imported. */
function RemoveDialog({
  opened,
  count,
  removing,
  message,
  onClose,
  onConfirm,
}: {
  opened: boolean;
  count: number;
  removing: boolean;
  message: string | undefined;
  onClose: () => void;
  onConfirm: () => void;
}) {
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={`Remove ${itemCountText(count)} from the ignore list?`}
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text data-testid="remove-count">
          Nothing is imported now. The next sync lists {count === 1 ? 'it' : 'each of them'} as New,
          and you choose then whether to copy {count === 1 ? 'it' : 'them'}.
        </Text>
        {message !== undefined && (
          <Text size="sm" role="alert" c="var(--mantine-color-error)">
            {message}
          </Text>
        )}
        <Group gap="sm" justify="flex-end">
          <Button variant="default" data-autofocus onClick={onClose}>
            Cancel
          </Button>
          <Button loading={removing} onClick={onConfirm}>
            Remove {itemCountText(count)}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** The items of one page, each with a checkbox, and one to select the whole page. */
function ItemTable({
  items,
  selected,
  timeZone,
  onSelect,
}: {
  items: IgnoredItem[];
  selected: string[];
  timeZone: string;
  onSelect: (selected: string[]) => void;
}) {
  const onPage = items.map((item) => item.sunoId);
  const chosenHere = onPage.filter((id) => selected.includes(id));
  const all = chosenHere.length === onPage.length;
  return (
    <Table.ScrollContainer minWidth={640}>
      <Table withTableBorder aria-label="Ignored Suno items">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col" w={40}>
              <Checkbox
                aria-label="Select every item on this page"
                checked={all}
                indeterminate={!all && chosenHere.length > 0}
                onChange={(event) => {
                  const others = selected.filter((id) => !onPage.includes(id));
                  onSelect(event.currentTarget.checked ? [...others, ...onPage] : others);
                }}
              />
            </Table.Th>
            <Table.Th scope="col">Suno title</Table.Th>
            <Table.Th scope="col">Suno ID</Table.Th>
            <Table.Th scope="col">Workspace</Table.Th>
            <Table.Th scope="col">Ignored</Table.Th>
            <Table.Th scope="col">Suno status</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {items.map((item) => {
            const title = item.title === null || item.title.trim() === '' ? 'Untitled' : item.title;
            return (
              <Table.Tr key={item.sunoId} data-item={item.sunoId}>
                <Table.Td>
                  <Checkbox
                    aria-label={`Select ${title} (${item.sunoId})`}
                    checked={selected.includes(item.sunoId)}
                    onChange={(event) => {
                      onSelect(
                        event.currentTarget.checked
                          ? [...selected, item.sunoId]
                          : selected.filter((id) => id !== item.sunoId),
                      );
                    }}
                  />
                </Table.Td>
                <Table.Th scope="row" fw="normal">
                  {title}
                </Table.Th>
                <Table.Td>
                  <Text size="sm" ff="monospace" span>
                    {item.sunoId}
                  </Text>
                </Table.Td>
                <Table.Td>
                  {item.workspaceId === null
                    ? 'None'
                    : workspaceText({ id: item.workspaceId, name: item.workspaceName })}
                </Table.Td>
                <Table.Td>{formatDate(item.ignoredAt, timeZone)}</Table.Td>
                <Table.Td data-testid="item-status">{statusText(item, timeZone)}</Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/**
 * Ignored Suno Items (#143), at `/suno/ignored`: the Suno clips the user chose not to copy (Don't
 * copy), apart from anything about local files. Each shows its Suno title, Suno ID, workspace, when
 * it was ignored, and its Suno status as last seen. The list is searched by title or Suno ID and
 * filtered by workspace and status, paged at 50, all of it in the address. Items are removed one or
 * many at a time after a confirmation; removing one imports nothing, it only makes the clip eligible
 * again at the next sync.
 */
export function IgnoredItemsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const query = ignoredQueryFrom(searchParams);
  const { state, reload } = useIgnoredItems(query);
  const timeZone = useConfiguredTimeZone();
  const [selected, setSelected] = useState<string[]>([]);
  const [confirming, setConfirming] = useState(false);
  // The count the confirmation states, fixed when it opens so it stays while the dialog closes.
  const [confirmCount, setConfirmCount] = useState(0);
  const [removing, setRemoving] = useState(false);
  const [message, setMessage] = useState<string>();
  const [removed, setRemoved] = useState<number>();
  // The page as last read: it stays shown while the list is read again (after a removal, say).
  const [known, setKnown] = useState<IgnoredPage>();
  if (state.phase === 'ready' && state.data !== known) {
    setKnown(state.data);
  }
  const page = state.phase === 'ready' ? state.data : state.phase === 'loading' ? known : undefined;
  const key = searchParams.toString();
  const pages = page === undefined ? 1 : Math.max(1, Math.ceil(page.total / page.pageSize));
  const filtered = query.q !== '' || query.workspace !== '' || query.status !== '';

  const change = (next: IgnoredQuery) => {
    setRemoved(undefined);
    setSearchParams(ignoredParameters(next));
  };

  const close = () => {
    setConfirming(false);
    setMessage(undefined);
  };

  const confirm = async () => {
    setRemoving(true);
    setMessage(undefined);
    const result = await removeIgnoredItems(selected);
    setRemoving(false);
    if (result.kind === 'failed') {
      setMessage(`Nothing was removed: ${FAILED_MESSAGE}`);
      return;
    }
    setConfirming(false);
    setSelected([]);
    setRemoved(result.removed);
    reload();
  };

  return (
    <Stack gap="lg">
      <Title order={2}>Ignored Suno items</Title>
      <Text>
        The Suno clips you chose not to copy (Don’t copy). Later syncs list them as Ignored, set to
        Don’t copy, and you can still import one from a review. Removing one here does not import
        it: the next sync lists it as New. These are Suno clips, not files on disk.
      </Text>
      <div role="status">
        {removed !== undefined && (
          <Text data-testid="items-removed">
            Removed {itemCountText(removed)} from the ignore list. Nothing was imported.
          </Text>
        )}
      </div>
      <Filters key={key} query={query} workspaces={page?.workspaces ?? []} onChange={change} />
      {page === undefined && state.phase === 'loading' && (
        <Loader aria-label="Loading the ignored Suno items" />
      )}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="The ignored Suno items could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {page?.total === 0 && (
        <Text data-testid="no-ignored-items">
          {filtered
            ? 'No ignored item matches.'
            : 'No Suno items are ignored. Choose Don’t copy for a record in a Suno import review to add one.'}
        </Text>
      )}
      {page !== undefined && page.items.length > 0 && (
        <>
          <Group justify="space-between" align="center">
            <Text size="sm" data-testid="selected-count">
              {selected.length === 0
                ? 'None selected'
                : `${itemCountText(selected.length)} selected`}{' '}
              of {itemCountText(page.total)}
            </Text>
            <Button
              disabled={selected.length === 0}
              onClick={() => {
                setRemoved(undefined);
                setConfirmCount(selected.length);
                setConfirming(true);
              }}
            >
              {selected.length === 0
                ? 'Remove selected items'
                : `Remove ${itemCountText(selected.length)}`}
            </Button>
          </Group>
          <ItemTable
            items={page.items}
            selected={selected}
            timeZone={timeZone}
            onSelect={setSelected}
          />
          {pages > 1 && (
            <Group component="nav" aria-label="Pages" justify="flex-end">
              <Pagination
                total={pages}
                value={page.page}
                onChange={(next) => {
                  change({ ...query, page: next });
                }}
                withEdges
                getItemProps={(item) => ({ 'aria-label': `Page ${String(item)}` })}
                getControlProps={(control) => ({ 'aria-label': PAGE_CONTROL_LABELS[control] })}
              />
            </Group>
          )}
        </>
      )}
      <RemoveDialog
        opened={confirming}
        count={confirmCount}
        removing={removing}
        message={message}
        onClose={close}
        onConfirm={() => {
          void confirm();
        }}
      />
    </Stack>
  );
}
