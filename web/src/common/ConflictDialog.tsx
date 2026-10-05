import { Badge, Button, Group, Modal, Stack, Table, Text } from '@mantine/core';
import type { ReactNode } from 'react';

/** Who changed a field since the user loaded the record. */
export type ConflictChange = 'elsewhere' | 'yours' | 'both';

/** One field the dialog compares: the value on the user's side and the value there is now. */
export interface ConflictRow {
  key: string;
  label: string;
  /** The user's edit for their own field; for any other field, the value as they loaded it. */
  yours: ReactNode;
  current: ReactNode;
  change: ConflictChange;
}

const CHANGE_NOTES: Record<ConflictChange, string> = {
  elsewhere: 'Changed elsewhere',
  yours: 'Your change',
  both: 'Changed elsewhere and by you',
};

/**
 * Shown when a save is refused because the record changed since it was loaded: which fields
 * differ, with the user's value and the current value side by side, and a choice. "Reload" takes
 * the current record and drops the user's change; "Reapply my change" saves it on top of the
 * current record (labelled as replacing the other value when both changed the same field); "Keep
 * editing" goes back to the field with the typed text. The dialog cannot be dismissed without a
 * choice: Escape and the close control are "Keep editing". Every editing screen uses it through
 * `useRevisionedSave`.
 */
export function ConflictDialog({
  opened,
  subject,
  rows,
  onReload,
  onReapply,
  onKeepEditing,
}: {
  opened: boolean;
  /** What changed, as a sentence names it: "This Song". */
  subject: string;
  rows: ConflictRow[];
  onReload: () => void;
  onReapply: () => void;
  onKeepEditing: () => void;
}) {
  const replaces = rows.find((row) => row.change === 'both');

  return (
    <Modal
      opened={opened}
      onClose={onKeepEditing}
      closeOnClickOutside={false}
      title="Changed since you loaded it"
      centered
      size="xl"
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text>
          {subject} was changed since you loaded it, so your change was not saved. Compare the
          values and choose what to keep.
        </Text>
        <Table withTableBorder withColumnBorders verticalSpacing="sm" aria-label="Differences">
          <Table.Thead>
            <Table.Tr>
              <Table.Th scope="col">Field</Table.Th>
              <Table.Th scope="col">Yours</Table.Th>
              <Table.Th scope="col">Current</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {rows.map((row) => (
              <Table.Tr key={row.key} data-field={row.key}>
                <Table.Th scope="row" style={{ verticalAlign: 'top' }}>
                  <Stack gap={4} align="flex-start">
                    <span>{row.label}</span>
                    <Badge variant="default" radius="sm" tt="none" fw={400}>
                      {CHANGE_NOTES[row.change]}
                    </Badge>
                  </Stack>
                </Table.Th>
                <Table.Td style={{ verticalAlign: 'top' }}>{row.yours}</Table.Td>
                <Table.Td style={{ verticalAlign: 'top' }}>{row.current}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        <Group justify="end">
          <Button variant="default" onClick={onKeepEditing}>
            Keep editing
          </Button>
          <Button variant="default" onClick={onReload}>
            Reload
          </Button>
          <Button onClick={onReapply}>
            {replaces
              ? `Reapply my change, replacing the current ${replaces.label.toLowerCase()}`
              : 'Reapply my change'}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** A compared value as the dialog shows it: text in full with its line breaks, or "(empty)". */
export function ConflictValue({ value }: { value: string | null }) {
  return value === null || value === '' ? (
    <Text c="var(--n8-color-secondary-text)" fs="italic">
      (empty)
    </Text>
  ) : (
    <Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{value}</Text>
  );
}
