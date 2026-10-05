import { Button, Group, Loader, Text } from '@mantine/core';
import { autosaveText, type AutosaveStatus } from './useAutosave';

/**
 * Whether the Version's edits are stored, said in words (never by colour alone) in a status region
 * screen readers announce, with what the user can do about a save that did not go through: Try
 * again after a refusal or while retrying, Reload or Reapply after a conflict left undecided.
 */
export function AutosaveIndicator({
  status,
  onRetry,
  onReload,
  onReapply,
}: {
  status: AutosaveStatus;
  onRetry: () => void;
  onReload: () => void;
  onReapply: () => void;
}) {
  const notSaved = status.kind !== 'saved' && status.kind !== 'saving';
  const canRetry = status.kind === 'retrying' || (status.kind === 'stopped' && status.canRetry);

  return (
    <Group gap="sm" align="center" wrap="wrap" data-testid="autosave">
      {status.kind === 'saving' && <Loader size="xs" aria-hidden="true" />}
      <Text
        size="sm"
        role="status"
        fw={notSaved ? 700 : undefined}
        c={notSaved ? 'var(--mantine-color-error)' : 'var(--n8-color-secondary-text)'}
        style={{ flex: '1 1 16rem' }}
      >
        {autosaveText(status)}
      </Text>
      {canRetry && (
        <Button variant="default" size="compact-sm" onClick={onRetry}>
          Try again
        </Button>
      )}
      {status.kind === 'conflict' && (
        <>
          <Button variant="default" size="compact-sm" onClick={onReload}>
            Reload
          </Button>
          <Button size="compact-sm" onClick={onReapply}>
            Reapply my change
          </Button>
        </>
      )}
    </Group>
  );
}
