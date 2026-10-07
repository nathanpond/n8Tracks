import {
  Button,
  Checkbox,
  Group,
  Loader,
  Modal,
  Radio,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core';
import { useEffect, useState } from 'react';
import {
  readRecordDiff,
  type ImportChoice,
  type ImportRecord,
  type RecordDiff as Diff,
} from '../api/sunoImports';
import { Notice } from '../components/Notice';
import { diffValueText, fieldLabel } from './importReviewRules';

/** What the user chose for a Conflict's Generation: move it, keep it, or decide later. */
type ConflictAction = 'moveToNewVersion' | 'keep' | 'skip';

/** The fields a saved choice accepted, and a Conflict's action, as the dialog starts. */
function initialChoice(record: ImportRecord): { accepted: string[]; action: ConflictAction } {
  const choice = record.choice;
  if (choice !== null && 'acceptFields' in choice) {
    return {
      accepted: choice.acceptFields,
      action: choice.action === 'apply' ? 'skip' : choice.action,
    };
  }
  return { accepted: [], action: 'skip' };
}

/**
 * The side-by-side diff of a Changed or Conflict record (#141), in a dialog. Each provider field
 * that differs shows n8Tracks' value and Suno's; the user takes Suno's value field by field or all at
 * once, and the rest are declined. A Conflict also lists the creation inputs that differ and offers to
 * move the Generation to a new Version holding Suno's inputs, or keep it where it is; a Version's
 * inputs are never changed. Saving stores the choice; nothing changes until the import is confirmed.
 * Every difference is in words, never in colour alone.
 */
export function RecordDiff({
  exportId,
  record,
  busy,
  onClose,
  onSave,
}: {
  exportId: string;
  record: ImportRecord;
  busy: boolean;
  onClose: () => void;
  onSave: (choice: ImportChoice) => void;
}) {
  const [diff, setDiff] = useState<Diff | null>();
  const initial = initialChoice(record);
  const [accepted, setAccepted] = useState<string[]>(initial.accepted);
  const [action, setAction] = useState<ConflictAction>(initial.action);
  const title = record.title ?? 'Untitled';

  useEffect(() => {
    const controller = new AbortController();
    void readRecordDiff(exportId, record.sunoId, controller.signal).then((read) => {
      if (!controller.signal.aborted) {
        setDiff(read ?? null);
      }
    });
    return () => {
      controller.abort();
    };
  }, [exportId, record.sunoId]);

  const conflict = record.class === 'conflict';
  const fields = diff?.fields ?? [];
  const choice = (): ImportChoice => {
    if (!conflict) {
      return { action: 'apply', acceptFields: accepted };
    }
    return action === 'skip' ? { action: 'skip' } : { action, acceptFields: accepted };
  };

  return (
    <Modal
      opened
      onClose={onClose}
      title={`Differences for “${title}”`}
      size="xl"
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack>
        {diff === undefined && <Loader aria-label="Loading the differences" />}
        {diff === null && (
          <Notice title="The differences could not be loaded">
            <Text>n8Tracks did not answer as expected. Close this and try again.</Text>
          </Notice>
        )}
        {diff != null && conflict && (
          <Stack gap="xs" data-testid="diff-inputs">
            <Title order={3} size="h5">
              Creation inputs
            </Title>
            <Text size="sm">
              Suno’s copy of this clip was made with other inputs than its Version. A Version’s
              inputs are never changed: move the Generation to a new Version holding Suno’s inputs,
              or keep it where it is.
            </Text>
            <Table withTableBorder aria-label="Creation inputs that differ">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">Input</Table.Th>
                  <Table.Th scope="col">Its Version</Table.Th>
                  <Table.Th scope="col">Suno’s clip</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {diff.inputs.map((input) => (
                  <Table.Tr key={input.field} data-input={input.field}>
                    <Table.Th scope="row">{input.field}</Table.Th>
                    <Table.Td style={{ whiteSpace: 'pre-wrap' }}>{input.current}</Table.Td>
                    <Table.Td style={{ whiteSpace: 'pre-wrap' }}>{input.incoming}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
            <Radio.Group
              label="What to do with the Generation"
              value={action}
              onChange={(value) => {
                setAction(value);
              }}
            >
              <Stack gap={4} mt={4}>
                <Radio
                  value="moveToNewVersion"
                  label="Move it to a new Version holding Suno’s inputs"
                />
                <Radio value="keep" label="Keep it where it is" />
                <Radio value="skip" label="Decide later (leave it as it is)" />
              </Stack>
            </Radio.Group>
          </Stack>
        )}
        {diff != null && fields.length > 0 && (
          <Stack gap="xs" data-testid="diff-fields">
            <Title order={3} size="h5">
              {conflict ? 'Details that differ' : 'What differs'}
            </Title>
            <Table withTableBorder aria-label="Fields that differ">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th scope="col">Field</Table.Th>
                  <Table.Th scope="col">In n8Tracks</Table.Th>
                  <Table.Th scope="col">On Suno</Table.Th>
                  <Table.Th scope="col">Take Suno’s</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {fields.map((field) => {
                  const label = fieldLabel(field.field);
                  const taken = accepted.includes(field.field);
                  return (
                    <Table.Tr key={field.field} data-field={field.field}>
                      <Table.Th scope="row">{label}</Table.Th>
                      <Table.Td>{diffValueText(field.field, field.current)}</Table.Td>
                      <Table.Td>{diffValueText(field.field, field.incoming)}</Table.Td>
                      <Table.Td>
                        <Checkbox
                          label={taken ? 'Take' : 'Keep'}
                          aria-label={`Take Suno’s ${label}`}
                          checked={taken}
                          disabled={conflict && action === 'skip'}
                          onChange={(event) => {
                            const checked = event.currentTarget.checked;
                            setAccepted((current) =>
                              checked
                                ? [...current, field.field]
                                : current.filter((other) => other !== field.field),
                            );
                          }}
                        />
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
            <Group gap="xs">
              <Button
                variant="default"
                size="xs"
                disabled={conflict && action === 'skip'}
                onClick={() => {
                  setAccepted(fields.map((field) => field.field));
                }}
              >
                Take all from Suno
              </Button>
              <Button
                variant="default"
                size="xs"
                onClick={() => {
                  setAccepted([]);
                }}
              >
                Keep all as they are
              </Button>
            </Group>
            <Text size="sm" data-testid="diff-summary">
              {accepted.length === 0
                ? 'Nothing is taken from Suno.'
                : `Taken from Suno: ${accepted.map(fieldLabel).join(', ')}.`}{' '}
              Its rating, comments, and state are never changed.
            </Text>
          </Stack>
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            Cancel
          </Button>
          {!conflict && (
            <Button
              variant="default"
              disabled={busy}
              onClick={() => {
                onSave({ action: 'skip' });
              }}
            >
              Decide later
            </Button>
          )}
          <Button
            disabled={busy || diff == null}
            onClick={() => {
              onSave(choice());
            }}
          >
            Save choice
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
