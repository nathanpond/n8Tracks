import { Badge, Button, Group, NativeSelect, Stack, Text } from '@mantine/core';
import { useState } from 'react';
import type { Song } from '../../api/songs';
import {
  SUNO_WORKSPACE_KEY,
  useSunoWorkspaces,
  workspaceChoices,
  workspaceName,
  type SongWorkspace,
} from '../../api/sunoWorkspaces';
import type { FieldValue } from '../../api/saves';
import { ConflictValue } from '../../common/ConflictDialog';
import type { Save } from '../../common/SavedTextField';
import { saveError } from '../../common/useInPlaceEdit';
import { Notice } from '../../components/Notice';

/** What the Song's workspace warning says while Suno no longer offers it. */
export const UNAVAILABLE_WORKSPACE_DETAIL =
  'Suno no longer lists this workspace, or it is in Suno’s Trash. The Song keeps it; choose another workspace to move it.';

/** The badge for a workspace Suno no longer offers, in the Song header and its Details. */
export function UnavailableWorkspaceBadge({ workspace }: { workspace: SongWorkspace }) {
  return (
    <Badge
      size="sm"
      variant="default"
      radius="sm"
      tt="none"
      data-testid="workspace-unavailable"
      aria-label={`Suno workspace ${workspaceName(workspace)} is unavailable`}
    >
      Workspace unavailable
    </Badge>
  );
}

/** A workspace ID as the conflict dialog shows it: the workspace's name when the list knows it. */
export function WorkspaceValue({ id }: { id: FieldValue }) {
  const { state } = useSunoWorkspaces();
  const known =
    state.phase === 'ready' ? state.data.find((workspace) => workspace.id === id) : undefined;
  return (
    <ConflictValue value={id === null ? null : known === undefined ? id : workspaceName(known)} />
  );
}

/**
 * The Song's Suno workspace (#129): which one it lives in, chosen from the Available workspaces the
 * extension has reported, or none. It is saved as soon as it is chosen, under the Song's revision,
 * and always by the workspace's Suno ID. A workspace Suno no longer offers stays the Song's and shows
 * a warning; it can only be left for an Available one.
 */
export function WorkspaceSection({ song, save }: { song: Song; save: Save }) {
  const { state, reload } = useSunoWorkspaces();
  const [pending, setPending] = useState<string>();
  const [error, setError] = useState<string>();
  const current = song.sunoWorkspace;
  const listed = state.phase === 'ready' ? state.data : undefined;
  const choices = workspaceChoices(listed ?? [], current);
  const busy = pending !== undefined;
  const data = [
    { value: '', label: 'None' },
    ...choices.map((choice) => ({
      value: choice.id,
      label:
        choice.state === 'available'
          ? workspaceName(choice)
          : `${workspaceName(choice)} (unavailable)`,
    })),
  ];

  const choose = async (value: string) => {
    setPending(value);
    setError(undefined);
    const outcome = await save(SUNO_WORKSPACE_KEY, value === '' ? null : value);
    setPending(undefined);
    setError(saveError(outcome, SUNO_WORKSPACE_KEY));
    if (outcome.kind === 'invalid') {
      // The list changed meanwhile (a workspace went or became unavailable): read it again.
      reload();
    }
  };

  return (
    <Stack gap={4} role="group" aria-labelledby="song-workspace" data-testid="song-workspace">
      <Group gap="xs">
        <Text fw={500} size="sm" id="song-workspace">
          Suno workspace
        </Text>
        {current?.state === 'unavailable' && <UnavailableWorkspaceBadge workspace={current} />}
      </Group>
      <NativeSelect
        label="Workspace"
        description="Where Generate on Suno saves this Song’s results."
        data={data}
        value={pending ?? current?.id ?? ''}
        disabled={busy || (listed === undefined && current === null)}
        onChange={(event) => {
          void choose(event.currentTarget.value);
        }}
        error={error}
        aria-invalid={error !== undefined}
      />
      {current?.state === 'unavailable' && (
        <div data-testid="workspace-warning">
          <Notice title={`${workspaceName(current)} is unavailable`}>
            <Text size="sm">{UNAVAILABLE_WORKSPACE_DETAIL}</Text>
          </Notice>
        </div>
      )}
      {listed?.length === 0 && (
        <Text size="sm" c="var(--n8-color-secondary-text)" data-testid="no-workspaces">
          No Suno workspaces are known yet: they appear once the browser extension reports them.
        </Text>
      )}
      {(state.phase === 'error' || state.phase === 'not-found') && (
        <Group gap="xs">
          <Text size="sm" c="var(--mantine-color-error)">
            The workspace list could not be loaded.
          </Text>
          <Button variant="default" size="compact-xs" onClick={reload}>
            Try again
          </Button>
        </Group>
      )}
    </Stack>
  );
}
