import {
  Badge,
  Button,
  Group,
  Loader,
  Modal,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
  VisuallyHidden,
} from '@mantine/core';
import { useRef, useState, type SyntheticEvent } from 'react';
import {
  createRelationshipType,
  deleteRelationshipType,
  normaliseRelationshipName,
  readRelationshipTypes,
  RELATIONSHIP_NAME_MAXIMUM_LENGTH,
  relationshipNameError,
  relationshipNameKey,
  renameRelationshipType,
  typeWithName,
  useRelationshipTypes,
  type RelationshipType,
  type RelationshipTypeResult,
} from '../api/relationships';
import { Notice } from '../components/Notice';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

const CONFLICT_MESSAGE =
  'The relationship types were changed somewhere else, so the list has been reloaded. Your change was not applied: check the list and try again.';

/** A type's two names as one label: "Sequel to / Has sequel", or the one name of a symmetric type. */
function typeLabel(type: Pick<RelationshipType, 'name' | 'reverseName' | 'symmetric'>): string {
  return type.symmetric ? type.name : `${type.name} / ${type.reverseName}`;
}

/** The message for a name another type already uses. */
function takenMessage(holder: RelationshipType): string {
  return `${holder.system ? 'The system type' : 'The type'} ${typeLabel(holder)} already uses this name. Choose another.`;
}

/** The errors of a pair of names before they are sent: each by the name rule, and neither used by another type. */
function namesErrors(
  name: string,
  reverseName: string,
  types: readonly RelationshipType[],
  except?: string,
): { name?: string; reverseName?: string } {
  const errors: { name?: string; reverseName?: string } = {
    name: relationshipNameError(name),
    reverseName: relationshipNameError(reverseName),
  };
  if (errors.name === undefined) {
    const holder = typeWithName(types, name, except);
    errors.name = holder && takenMessage(holder);
  }
  const symmetric = relationshipNameKey(name) === relationshipNameKey(reverseName);
  if (errors.reverseName === undefined && !symmetric) {
    const holder = typeWithName(types, reverseName, except);
    errors.reverseName = holder && takenMessage(holder);
  }
  return errors;
}

/** The two name fields a type is added or renamed with. */
function NameFields({
  name,
  reverseName,
  errors,
  onName,
  onReverseName,
  focusFirst = false,
}: {
  name: string;
  reverseName: string;
  errors: { name?: string; reverseName?: string };
  onName: (value: string) => void;
  onReverseName: (value: string) => void;
  focusFirst?: boolean;
}) {
  return (
    <>
      <TextInput
        label="Name"
        description={`As the Song it starts from shows it, for example "Sequel to". Up to ${String(RELATIONSHIP_NAME_MAXIMUM_LENGTH)} characters.`}
        value={name}
        error={errors.name}
        data-autofocus={focusFirst || undefined}
        onChange={(event) => {
          onName(event.currentTarget.value);
        }}
      />
      <TextInput
        label="Reverse name"
        description='As the other Song shows it, for example "Has sequel". Give the same name for a type that reads the same both ways.'
        value={reverseName}
        error={errors.reverseName}
        onChange={(event) => {
          onReverseName(event.currentTarget.value);
        }}
      />
    </>
  );
}

/** Adds a type from its two names. */
function AddTypeForm({
  types,
  onAdd,
}: {
  types: RelationshipType[];
  onAdd: (name: string, reverseName: string) => Promise<RelationshipTypeResult>;
}) {
  const [name, setName] = useState('');
  const [reverseName, setReverseName] = useState('');
  const [errors, setErrors] = useState<{ name?: string; reverseName?: string }>({});
  const [saving, setSaving] = useState(false);

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (saving) {
      return;
    }
    const found = namesErrors(name, reverseName, types);
    setErrors(found);
    if (found.name !== undefined || found.reverseName !== undefined) {
      return;
    }
    setSaving(true);
    const result = await onAdd(name, reverseName);
    setSaving(false);
    if (result.kind === 'saved') {
      setName('');
      setReverseName('');
    } else if (result.kind === 'invalid') {
      setErrors({
        name: result.errors.name?.join(' '),
        reverseName: result.errors.reverseName?.join(' '),
      });
    }
  };

  return (
    <form
      noValidate
      aria-labelledby="add-relationship-type"
      onSubmit={(event) => {
        void submit(event);
      }}
    >
      <Stack gap="sm">
        <Title order={3} size="h4" id="add-relationship-type">
          Add a type
        </Title>
        <NameFields
          name={name}
          reverseName={reverseName}
          errors={errors}
          onName={(value) => {
            setName(value);
            setErrors((now) => ({ ...now, name: undefined }));
          }}
          onReverseName={(value) => {
            setReverseName(value);
            setErrors((now) => ({ ...now, reverseName: undefined }));
          }}
        />
        <Group>
          <Button type="submit" loading={saving}>
            Add type
          </Button>
        </Group>
      </Stack>
    </form>
  );
}

/** Renames one of the user's types; both names are sent. */
function EditDialog({
  type,
  types,
  onClose,
  onSave,
}: {
  type: RelationshipType | undefined;
  types: RelationshipType[];
  onClose: () => void;
  onSave: (
    type: RelationshipType,
    name: string,
    reverseName: string,
  ) => Promise<RelationshipTypeResult>;
}) {
  const [name, setName] = useState(type?.name ?? '');
  const [reverseName, setReverseName] = useState(type?.reverseName ?? '');
  const [errors, setErrors] = useState<{ name?: string; reverseName?: string }>({});
  const [saving, setSaving] = useState(false);

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (type === undefined || saving) {
      return;
    }
    const found = namesErrors(name, reverseName, types, type.id);
    setErrors(found);
    if (found.name !== undefined || found.reverseName !== undefined) {
      return;
    }
    if (
      normaliseRelationshipName(name) === type.name &&
      normaliseRelationshipName(reverseName) === type.reverseName
    ) {
      onClose();
      return;
    }
    setSaving(true);
    const result = await onSave(type, name, reverseName);
    setSaving(false);
    if (result.kind === 'invalid') {
      setErrors({
        name: result.errors.name?.join(' ') ?? FAILED_MESSAGE,
        reverseName: result.errors.reverseName?.join(' '),
      });
    } else if (result.kind === 'failed') {
      setErrors({ name: FAILED_MESSAGE });
    } else {
      onClose();
    }
  };

  return (
    <Modal
      opened={type !== undefined}
      onClose={onClose}
      title={type ? `Rename ${typeLabel(type)}` : 'Rename type'}
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <form
        noValidate
        onSubmit={(event) => {
          void submit(event);
        }}
      >
        <Stack gap="md">
          <NameFields
            name={name}
            reverseName={reverseName}
            errors={errors}
            focusFirst
            onName={(value) => {
              setName(value);
              setErrors((now) => ({ ...now, name: undefined }));
            }}
            onReverseName={(value) => {
              setReverseName(value);
              setErrors((now) => ({ ...now, reverseName: undefined }));
            }}
          />
          <Text size="sm">Every Song related by this type shows the new names.</Text>
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

/** Deleting a type that relationships use: they are removed with it. The dialog says how many. */
function DeleteDialog({
  target,
  onClose,
  onDelete,
}: {
  target: { type: RelationshipType; count: number } | undefined;
  onClose: () => void;
  onDelete: (type: RelationshipType) => Promise<RelationshipTypeResult>;
}) {
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const count = target?.count ?? 0;
  const label = target ? typeLabel(target.type) : '';

  const confirm = async () => {
    if (target === undefined) {
      return;
    }
    setDeleting(true);
    const result = await onDelete(target.type);
    setDeleting(false);
    if (result.kind === 'failed' || result.kind === 'invalid') {
      setError(FAILED_MESSAGE);
    } else {
      onClose();
    }
  };

  return (
    <Modal
      opened={target !== undefined}
      onClose={onClose}
      title={`Delete ${label}`}
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text data-testid="delete-summary">
          {count === 1 ? '1 relationship uses' : `${String(count)} relationships use`}{' '}
          <strong>{label}</strong>. Deleting the type removes{' '}
          {count === 1 ? 'that relationship' : `those ${String(count)} relationships`} from their
          Songs. The Songs themselves are not changed otherwise.
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
            Delete {target?.type.name ?? ''}
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
function RelationshipsEditor({ initial }: { initial: RelationshipType[] }) {
  const [types, setTypes] = useState(initial);
  const [message, setMessage] = useState<Message | undefined>();
  const [editing, setEditing] = useState<RelationshipType | undefined>();
  const [deleting, setDeleting] = useState<{ type: RelationshipType; count: number }>();
  const busy = useRef(false);

  const refresh = async () => {
    const list = await readRelationshipTypes();
    if (list !== undefined) {
      setTypes(list);
    }
  };

  /** Runs one change at a time, then reads the list again; answers what came back. */
  const run = async (
    send: () => Promise<RelationshipTypeResult>,
    done: (result: RelationshipTypeResult) => string,
  ): Promise<RelationshipTypeResult> => {
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
      case 'system':
        setMessage({ text: CONFLICT_MESSAGE, tone: 'problem' });
        await refresh();
        break;
      case 'failed':
        setMessage({ text: FAILED_MESSAGE, tone: 'problem' });
        break;
      default:
        // `invalid` and `in-use` are shown where the change was asked for.
        break;
    }
    return result;
  };

  const remove = async (type: RelationshipType) => {
    if (type.relationshipCount > 0) {
      setDeleting({ type, count: type.relationshipCount });
      return;
    }
    const result = await run(
      () => deleteRelationshipType(type),
      () => `${typeLabel(type)} is deleted.`,
    );
    if (result.kind === 'in-use') {
      // Songs were related by it since the list was loaded.
      setDeleting({ type, count: result.relationshipCount });
    }
  };

  return (
    <Stack gap="lg">
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

      <Table.ScrollContainer minWidth={520}>
        <Table aria-label="Relationship types" highlightOnHover>
          <Table.Thead>
            <Table.Tr>
              <Table.Th scope="col">Name</Table.Th>
              <Table.Th scope="col">Reverse name</Table.Th>
              <Table.Th scope="col" ta="end">
                Relationships
              </Table.Th>
              <Table.Th scope="col">
                <VisuallyHidden>Actions</VisuallyHidden>
              </Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {types.map((type) => (
              <Table.Tr
                key={type.id}
                data-type-name={type.name}
                data-system={type.system ? 'true' : undefined}
              >
                <Table.Th scope="row" fw={400}>
                  {type.name}
                </Table.Th>
                <Table.Td>
                  {type.symmetric ? <Text size="sm">Same both ways</Text> : type.reverseName}
                </Table.Td>
                <Table.Td ta="end">{type.relationshipCount}</Table.Td>
                <Table.Td>
                  {type.system ? (
                    <Group justify="end">
                      <Badge variant="outline" color="gray">
                        System type
                      </Badge>
                    </Group>
                  ) : (
                    <Group gap={6} justify="end" wrap="nowrap">
                      <Button
                        size="xs"
                        variant="default"
                        aria-label={`Rename ${type.name}`}
                        onClick={() => {
                          setEditing(type);
                        }}
                      >
                        Rename
                      </Button>
                      <Button
                        size="xs"
                        variant="default"
                        color="red"
                        aria-label={`Delete ${type.name}`}
                        onClick={() => {
                          void remove(type);
                        }}
                      >
                        Delete
                      </Button>
                    </Group>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
      <Text size="sm" c="var(--n8-color-secondary-text)">
        System types stand for Suno&apos;s lineage actions, plus Remix for imported clips whose
        action is not recognised and Derived From. They cannot be renamed or deleted, but you can
        use them on Songs.
      </Text>

      <AddTypeForm
        types={types}
        onAdd={(name, reverseName) =>
          run(
            () => createRelationshipType(name, reverseName),
            (result) =>
              result.kind === 'saved'
                ? `${typeLabel(result.type)} is added.`
                : 'The type is added.',
          )
        }
      />

      <EditDialog
        key={`edit-${editing?.id ?? 'none'}`}
        type={editing}
        types={types}
        onClose={() => {
          setEditing(undefined);
        }}
        onSave={(type, name, reverseName) =>
          run(
            () => renameRelationshipType(type, name, reverseName),
            (result) =>
              result.kind === 'saved'
                ? `${typeLabel(type)} is renamed to ${typeLabel(result.type)}.`
                : 'The type is renamed.',
          )
        }
      />
      <DeleteDialog
        key={`delete-${deleting?.type.id ?? 'none'}`}
        target={deleting}
        onClose={() => {
          setDeleting(undefined);
        }}
        onDelete={(type) =>
          run(
            () => deleteRelationshipType(type, true),
            () => `${typeLabel(type)} is deleted, with its relationships.`,
          )
        }
      />
    </Stack>
  );
}

/**
 * Settings → Relationships: the relationship types Songs are related by. The system types (Suno's
 * lineage actions, Remix, and Derived From) come first and cannot be changed; the user adds their
 * own, each with a name for both directions, renames them, and deletes them. A type in use is
 * deleted with its relationships after a confirmation that says how many.
 */
export function RelationshipsPage() {
  const { state, reload } = useRelationshipTypes();

  return (
    <Stack gap="lg">
      <Title order={2}>Relationships</Title>
      <Text>
        How your Songs relate to each other. Each type has a name for both directions: a Song is
        &ldquo;Sequel to&rdquo; another, which &ldquo;Has sequel&rdquo;. Relate Songs from a
        Song&apos;s Details panel.
      </Text>
      {state.phase === 'loading' && <Loader aria-label="Loading relationship types" />}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Notice title="Relationship types could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {state.phase === 'ready' && <RelationshipsEditor initial={state.data} />}
    </Stack>
  );
}
