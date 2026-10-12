import { Box, Button, Checkbox, Group, Modal, SimpleGrid, Stack, Text } from '@mantine/core';
import { useState } from 'react';

/** One setting the Version holds that differs from the source's, as the prompt shows it. */
export interface SettingChoice {
  key: string;
  /** What the setting is called on the page. */
  label: string;
  /** The Version's value, written out. */
  current: string;
  /** The source's value, written out. */
  source: string;
  /** Whether the values are free text (lyrics, styles, a prompt): shown whole, in a scrolling box. */
  long: boolean;
}

/**
 * The one prompt choosing a Cover, Extend, or Remix source asks (#431) when the Version already
 * holds settings that differ from the source's: each such setting with the Version's value and the
 * source's, and a tick to take the source's. Unticked settings keep the Version's value. Confirming
 * sends the ticked ones (the empty settings are filled with them); cancelling changes nothing, the
 * source included. Long text is shown whole in a box that scrolls, and can be reached and scrolled
 * from the keyboard.
 */
export function SourceSettingsDialog({
  choices,
  filled,
  unknownNote,
  busy,
  error,
  onConfirm,
  onCancel,
}: {
  /** The differing settings; the prompt is open while there are any. */
  choices: readonly SettingChoice[] | undefined;
  /** The labels of the empty settings the source fills without asking. */
  filled: readonly string[];
  /** What the source's settings are not known for, when any are not. */
  unknownNote: string | undefined;
  busy: boolean;
  error: string | undefined;
  onConfirm: (take: string[]) => void;
  onCancel: () => void;
}) {
  return (
    <Modal
      opened={choices !== undefined && choices.length > 0}
      onClose={onCancel}
      title="Settings from the source"
      closeButtonProps={{ 'aria-label': 'Close' }}
      size="xl"
    >
      {choices !== undefined && choices.length > 0 && (
        <ChoiceForm
          key={choices.map((choice) => choice.key).join(',')}
          choices={choices}
          filled={filled}
          unknownNote={unknownNote}
          busy={busy}
          error={error}
          onConfirm={onConfirm}
          onCancel={onCancel}
        />
      )}
    </Modal>
  );
}

function ChoiceForm({
  choices,
  filled,
  unknownNote,
  busy,
  error,
  onConfirm,
  onCancel,
}: {
  choices: readonly SettingChoice[];
  filled: readonly string[];
  unknownNote: string | undefined;
  busy: boolean;
  error: string | undefined;
  onConfirm: (take: string[]) => void;
  onCancel: () => void;
}) {
  const [taken, setTaken] = useState<ReadonlySet<string>>(new Set());
  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        onConfirm(choices.map((choice) => choice.key).filter((key) => taken.has(key)));
      }}
    >
      <Stack gap="md">
        <Text size="sm" data-testid="source-settings-intro">
          This Version already holds settings that differ from the source’s. Tick each one to take
          from the source; the rest keep this Version’s value.
          {filled.length > 0 &&
            ` Its empty settings are filled from the source: ${filled.join(', ')}.`}
        </Text>
        {choices.map((choice) => (
          <Stack
            key={choice.key}
            gap="xs"
            role="group"
            aria-labelledby={`setting-${choice.key}-label`}
            data-setting={choice.key}
          >
            <Text fw={500} size="sm" id={`setting-${choice.key}-label`}>
              {choice.label}
            </Text>
            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
              <SettingValue
                heading="This Version"
                label={`${choice.label}: this Version’s value`}
                value={choice.current}
                long={choice.long}
              />
              <SettingValue
                heading="Source"
                label={`${choice.label}: the source’s value`}
                value={choice.source}
                long={choice.long}
              />
            </SimpleGrid>
            <Checkbox
              label={`Use the source’s ${choice.label}`}
              checked={taken.has(choice.key)}
              onChange={(event) => {
                const next = new Set(taken);
                if (event.currentTarget.checked) {
                  next.add(choice.key);
                } else {
                  next.delete(choice.key);
                }
                setTaken(next);
              }}
            />
          </Stack>
        ))}
        {unknownNote !== undefined && (
          <Text size="sm" data-testid="source-settings-unknown">
            {unknownNote}
          </Text>
        )}
        {error !== undefined && (
          <Text size="sm" role="alert" c="var(--n8-notice-text)">
            {error}
          </Text>
        )}
        <Group justify="flex-end" gap="sm">
          <Button variant="default" onClick={onCancel} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" loading={busy}>
            Confirm
          </Button>
        </Group>
      </Stack>
    </form>
  );
}

/** One side of a setting: its value whole, long text in a box that scrolls (and takes focus to be scrolled). */
function SettingValue({
  heading,
  label,
  value,
  long,
}: {
  heading: string;
  label: string;
  value: string;
  long: boolean;
}) {
  return (
    <Stack gap={2}>
      <Text size="xs" c="var(--n8-color-secondary-text)">
        {heading}
      </Text>
      {long ? (
        <Box
          component="pre"
          role="region"
          aria-label={label}
          tabIndex={0}
          m={0}
          p="xs"
          style={{
            maxHeight: '12rem',
            overflow: 'auto',
            whiteSpace: 'pre-wrap',
            overflowWrap: 'anywhere',
            fontFamily: 'inherit',
            fontSize: 'var(--mantine-font-size-sm)',
            border: '1px solid var(--mantine-color-default-border)',
            borderRadius: 'var(--mantine-radius-sm)',
          }}
        >
          {value}
        </Box>
      ) : (
        <Text size="sm" style={{ overflowWrap: 'anywhere' }}>
          {value}
        </Text>
      )}
    </Stack>
  );
}
