import {
  Badge,
  Button,
  Checkbox,
  Group,
  Loader,
  Modal,
  Paper,
  Radio,
  SegmentedControl,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useClipboard } from '@mantine/hooks';
import { useState, type SyntheticEvent } from 'react';
import {
  CREDENTIAL_KINDS,
  CREDENTIAL_SCOPES,
  createCredential,
  renameCredential,
  revokeCredential,
  useCredentials,
  type Credential,
  type CredentialKind,
} from '../api/credentials';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { Notice } from '../components/Notice';

const KIND_LABELS: Record<CredentialKind, string> = {
  api: 'API',
  extension: 'Extension',
  'mcp-gateway': 'MCP gateway',
};

const SCOPE_DESCRIPTIONS: Record<(typeof CREDENTIAL_SCOPES)[number], string> = {
  'catalog.read': 'Read Songs, Versions, collections, and jobs',
  'songs.write': 'Create and edit Songs',
  'versions.write': 'Create and edit Versions',
  'collections.write': 'Create and edit collections',
  'generations.evaluate': 'Rate and annotate Generations',
  'artwork.write': 'Add and change artwork',
  'catalog.bulk-write': 'Change many records in one request',
  'suno.sync': 'Browser extension: send your Suno library to n8Tracks for review',
  'suno.generate': 'Browser extension: fill Suno’s Create form and report what it made',
};

export const TOKEN_SHOWN_ONCE_MESSAGE =
  'This is the only time the token is shown. Copy it now and keep it somewhere safe: n8Tracks keeps only a fingerprint of it and cannot show it again.';

const FAILED_MESSAGE =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

function kindLabel(kind: string): string {
  return (KIND_LABELS as Record<string, string | undefined>)[kind] ?? kind;
}

type KindFilter = 'all' | CredentialKind;

/** What the extension last reported with this credential: its version, its adapter version, and when. */
function extensionSeen(credential: Credential, timeZone: string): string {
  if (!credential.lastSeenAt) {
    return 'Extension not connected yet';
  }
  const extension = credential.lastExtensionVersion ?? 'unknown';
  const adapter = credential.lastAdapterVersion ?? 'unknown';
  return `Extension ${extension}, adapter ${adapter}, seen ${formatDateTime(credential.lastSeenAt, timeZone)}`;
}

/** The create dialog's form, then the shown-once token. Closing it forgets the token. */
function CreateCredentialDialog({
  opened,
  onClose,
  onCreated,
}: {
  opened: boolean;
  onClose: () => void;
  onCreated: () => void;
}) {
  const [name, setName] = useState('');
  const [kind, setKind] = useState<CredentialKind>('api');
  const [scopes, setScopes] = useState<string[]>([]);
  const [errors, setErrors] = useState<Record<string, string | undefined>>({});
  const [failed, setFailed] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [created, setCreated] = useState<{ name: string; token: string } | undefined>();
  const clipboard = useClipboard({ timeout: 4000 });

  const close = () => {
    setName('');
    setKind('api');
    setScopes([]);
    setErrors({});
    setFailed(false);
    setCreated(undefined);
    clipboard.reset();
    onClose();
  };

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    setSubmitting(true);
    setFailed(false);
    const result = await createCredential({ name, kind, scopes });
    setSubmitting(false);

    switch (result.kind) {
      case 'created':
        setErrors({});
        setCreated({ name: result.credential.name, token: result.token });
        onCreated();
        return;
      case 'invalid':
        setErrors({
          name: result.errors.name?.join(' '),
          kind: result.errors.kind?.join(' '),
          scopes: result.errors.scopes?.join(' '),
        });
        return;
      default:
        setErrors({});
        setFailed(true);
    }
  };

  return (
    <Modal
      opened={opened}
      onClose={close}
      title={created ? 'Copy the new token' : 'Create credential'}
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
      size="lg"
      closeOnClickOutside={!created}
    >
      {created ? (
        <Stack gap="md">
          <Text>
            The credential <strong>{created.name}</strong> was created.
          </Text>
          <Notice title="Shown once">
            <Text>{TOKEN_SHOWN_ONCE_MESSAGE}</Text>
          </Notice>
          <Group align="end" wrap="nowrap">
            <TextInput
              label="Token"
              value={created.token}
              readOnly
              style={{ flex: 1 }}
              styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
              onFocus={(event) => {
                event.currentTarget.select();
              }}
            />
            <Button
              onClick={() => {
                clipboard.copy(created.token);
              }}
            >
              {clipboard.copied ? 'Copied' : 'Copy token'}
            </Button>
          </Group>
          <div role="status">
            {clipboard.copied && <Text size="sm">The token is on the clipboard.</Text>}
            {clipboard.error && (
              <Text size="sm">Copying failed. Select the token and copy it yourself.</Text>
            )}
          </div>
          <Group justify="end">
            <Button onClick={close}>Done</Button>
          </Group>
        </Stack>
      ) : (
        <form
          noValidate
          aria-label="Create credential"
          onSubmit={(event) => {
            void submit(event);
          }}
        >
          <Stack gap="md">
            <TextInput
              label="Name"
              description="What uses it, so you recognise it later. Up to 100 characters."
              required
              maxLength={100}
              value={name}
              onChange={(event) => {
                setName(event.currentTarget.value);
              }}
              error={errors.name}
              aria-invalid={errors.name !== undefined}
              data-autofocus
            />
            <Radio.Group
              label="Kind"
              description="What the credential is for. It does not change what the token may do."
              required
              value={kind}
              onChange={(value) => {
                setKind(value);
              }}
              error={errors.kind}
            >
              <Group mt="xs">
                {CREDENTIAL_KINDS.map((value) => (
                  <Radio key={value} value={value} label={KIND_LABELS[value]} />
                ))}
              </Group>
            </Radio.Group>
            <Checkbox.Group
              label="Scopes"
              description="What the token may do. Scopes cannot be changed later: revoke the credential and create a new one instead."
              required
              value={scopes}
              onChange={setScopes}
              error={errors.scopes}
            >
              <Stack gap="xs" mt="xs">
                {CREDENTIAL_SCOPES.map((scope) => (
                  <Checkbox
                    key={scope}
                    value={scope}
                    label={scope}
                    description={SCOPE_DESCRIPTIONS[scope]}
                  />
                ))}
              </Stack>
            </Checkbox.Group>
            <div role="status">
              {failed && (
                <Notice title="Credential not created">
                  <Text>{FAILED_MESSAGE}</Text>
                </Notice>
              )}
            </div>
            <Group justify="end">
              <Button variant="default" onClick={close}>
                Cancel
              </Button>
              <Button type="submit" loading={submitting}>
                Create credential
              </Button>
            </Group>
          </Stack>
        </form>
      )}
    </Modal>
  );
}

/** Renames a credential in force. A conflict reloads the list and says why. */
function RenameDialog({
  credential,
  onClose,
  onChanged,
}: {
  credential: Credential | undefined;
  onClose: () => void;
  onChanged: () => void;
}) {
  const [name, setName] = useState(credential?.name ?? '');
  const [error, setError] = useState<string | undefined>();
  const [notice, setNotice] = useState<string | undefined>();
  const [submitting, setSubmitting] = useState(false);

  const submit = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!credential) {
      return;
    }
    setSubmitting(true);
    setNotice(undefined);
    const result = await renameCredential(credential, name);
    setSubmitting(false);

    switch (result.kind) {
      case 'done':
        onChanged();
        onClose();
        return;
      case 'invalid':
        setError(result.errors.name?.join(' '));
        return;
      case 'conflict':
        setError(undefined);
        setNotice(
          result.revoked
            ? 'This credential has been revoked, and a revoked credential keeps its name.'
            : `This credential was changed elsewhere: it is now called “${result.current.name}”. Close this and try again.`,
        );
        onChanged();
        return;
      case 'not-found':
        setError(undefined);
        setNotice('This credential no longer exists.');
        onChanged();
        return;
      case 'failed':
        setError(undefined);
        setNotice(FAILED_MESSAGE);
    }
  };

  return (
    <Modal
      opened={credential !== undefined}
      onClose={onClose}
      title="Rename credential"
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <form
        noValidate
        aria-label="Rename credential"
        onSubmit={(event) => {
          void submit(event);
        }}
      >
        <Stack gap="md">
          <TextInput
            label="Name"
            required
            maxLength={100}
            value={name}
            onChange={(event) => {
              setName(event.currentTarget.value);
            }}
            error={error}
            aria-invalid={error !== undefined}
            data-autofocus
          />
          <div role="status">
            {notice && (
              <Notice title="Not renamed">
                <Text>{notice}</Text>
              </Notice>
            )}
          </div>
          <Group justify="end">
            <Button variant="default" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={submitting}>
              Save name
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}

/** Asks before revoking: it cannot be undone. */
function RevokeDialog({
  credential,
  onClose,
  onChanged,
}: {
  credential: Credential | undefined;
  onClose: () => void;
  onChanged: () => void;
}) {
  const [failed, setFailed] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const revoke = async () => {
    if (!credential) {
      return;
    }
    setSubmitting(true);
    setFailed(false);
    const result = await revokeCredential(credential.id);
    setSubmitting(false);
    if (result.kind === 'done' || result.kind === 'not-found') {
      onChanged();
      onClose();
    } else {
      setFailed(true);
    }
  };

  return (
    <Modal
      opened={credential !== undefined}
      onClose={onClose}
      title="Revoke credential?"
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text>
          Anything using <strong>{credential?.name}</strong> stops working at once. Revoking cannot
          be undone: to give access again, create a new credential.
        </Text>
        <div role="status">
          {failed && (
            <Notice title="Credential not revoked">
              <Text>{FAILED_MESSAGE}</Text>
            </Notice>
          )}
        </div>
        <Group justify="end">
          <Button variant="default" onClick={onClose} data-autofocus>
            Cancel
          </Button>
          <Button
            color="red"
            loading={submitting}
            onClick={() => {
              void revoke();
            }}
          >
            Revoke credential
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function CredentialRow({
  credential,
  timeZone,
  onRename,
  onRevoke,
}: {
  credential: Credential;
  timeZone: string;
  onRename: () => void;
  onRevoke: () => void;
}) {
  const revoked = credential.revokedUtc !== null;
  return (
    <Table.Tr data-credential={credential.name}>
      <Table.Th scope="row">{credential.name}</Table.Th>
      <Table.Td>{kindLabel(credential.kind)}</Table.Td>
      <Table.Td>
        <Group gap={4}>
          {credential.scopes.map((scope) => (
            <Badge key={scope} variant="outline" color="gray" tt="none">
              {scope}
            </Badge>
          ))}
        </Group>
      </Table.Td>
      <Table.Td>{formatDateTime(credential.createdUtc, timeZone)}</Table.Td>
      <Table.Td>
        {credential.lastUsedUtc ? formatDateTime(credential.lastUsedUtc, timeZone) : 'Never'}
        {credential.kind === 'extension' && (
          <Text size="sm" c="var(--n8-color-secondary-text)" data-testid="extension-seen">
            {extensionSeen(credential, timeZone)}
          </Text>
        )}
      </Table.Td>
      <Table.Td>
        {credential.revokedUtc ? (
          <Text size="sm">Revoked {formatDateTime(credential.revokedUtc, timeZone)}</Text>
        ) : (
          <Text size="sm">Active</Text>
        )}
      </Table.Td>
      <Table.Td>
        {!revoked && (
          <Group gap="xs" wrap="nowrap">
            <Button
              size="xs"
              variant="default"
              onClick={onRename}
              aria-label={`Rename ${credential.name}`}
            >
              Rename
            </Button>
            <Button
              size="xs"
              variant="default"
              color="red"
              onClick={onRevoke}
              aria-label={`Revoke ${credential.name}`}
            >
              Revoke
            </Button>
          </Group>
        )}
      </Table.Td>
    </Table.Tr>
  );
}

function CredentialTable({
  credentials,
  timeZone,
  onRename,
  onRevoke,
}: {
  credentials: Credential[];
  timeZone: string;
  onRename: (credential: Credential) => void;
  onRevoke: (credential: Credential) => void;
}) {
  return (
    <Table.ScrollContainer minWidth={760}>
      <Table withTableBorder aria-label="Credentials">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">Name</Table.Th>
            <Table.Th scope="col">Kind</Table.Th>
            <Table.Th scope="col">Scopes</Table.Th>
            <Table.Th scope="col">Created</Table.Th>
            <Table.Th scope="col">Last used</Table.Th>
            <Table.Th scope="col">Status</Table.Th>
            <Table.Th scope="col">Actions</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {credentials.map((credential) => (
            <CredentialRow
              key={credential.id}
              credential={credential}
              timeZone={timeZone}
              onRename={() => {
                onRename(credential);
              }}
              onRevoke={() => {
                onRevoke(credential);
              }}
            />
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/**
 * Settings → Credentials: tokens for the API, the browser extension, and the MCP gateway. A token
 * is shown once, when it is created; afterwards only its name, kind, scopes, and dates are. A
 * revoked credential stays in the list, marked with when it was revoked.
 */
export function CredentialsPage() {
  const { state, reload } = useCredentials();
  const timeZone = useConfiguredTimeZone();
  const [filter, setFilter] = useState<KindFilter>('all');
  const [creating, setCreating] = useState(false);
  const [renaming, setRenaming] = useState<Credential | undefined>();
  const [revoking, setRevoking] = useState<Credential | undefined>();

  const shown =
    state.phase === 'ready'
      ? state.credentials.filter((credential) => filter === 'all' || credential.kind === filter)
      : [];

  return (
    <Stack gap="lg">
      <Title order={2}>Credentials</Title>
      <Text>
        A credential lets the API, the browser extension, or the MCP gateway act for you with only
        the scopes you give it. None of them can sign in here, manage credentials, or change your
        password. A token never expires: revoke it to end it.
      </Text>
      <Group justify="space-between">
        <SegmentedControl
          aria-label="Show kind"
          value={filter}
          onChange={(value) => {
            setFilter(value);
          }}
          data={[
            { value: 'all', label: 'All kinds' },
            ...CREDENTIAL_KINDS.map((kind) => ({ value: kind, label: KIND_LABELS[kind] })),
          ]}
        />
        <Button
          onClick={() => {
            setCreating(true);
          }}
        >
          Create credential
        </Button>
      </Group>

      {state.phase === 'loading' && <Loader aria-label="Loading credentials" />}
      {state.phase === 'error' && (
        <Notice title="Credentials could not be loaded">
          <Text>{FAILED_MESSAGE}</Text>
          <div>
            <Button variant="default" size="xs" onClick={reload}>
              Try again
            </Button>
          </div>
        </Notice>
      )}
      {state.phase === 'ready' &&
        (shown.length === 0 ? (
          <Paper p="sm" withBorder>
            <Text>
              {filter === 'all'
                ? 'There are no credentials yet.'
                : `There are no ${KIND_LABELS[filter]} credentials.`}
            </Text>
          </Paper>
        ) : (
          <CredentialTable
            credentials={shown}
            timeZone={timeZone}
            onRename={setRenaming}
            onRevoke={setRevoking}
          />
        ))}

      <CreateCredentialDialog
        opened={creating}
        onClose={() => {
          setCreating(false);
        }}
        onCreated={reload}
      />
      <RenameDialog
        key={`rename-${renaming?.id ?? 'none'}`}
        credential={renaming}
        onClose={() => {
          setRenaming(undefined);
        }}
        onChanged={reload}
      />
      <RevokeDialog
        key={`revoke-${revoking?.id ?? 'none'}`}
        credential={revoking}
        onClose={() => {
          setRevoking(undefined);
        }}
        onChanged={reload}
      />
    </Stack>
  );
}
