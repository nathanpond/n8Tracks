import { Chip, Group, SimpleGrid, Stack, Text } from '@mantine/core';
import { useId } from 'react';
import type { CreateField } from '../../api/createFields';
import { NumberControl } from './OptionControls';
import { optionParts, type KindOptionsProps } from './optionParts';

const KEY_OPTION = 'soundKey';
const SCALE_OPTION = 'soundScale';
const ANY_KEY = 'any';

/** How Suno writes a Sound's types. */
const TYPE_LABELS: Readonly<Record<string, string>> = { one_shot: 'One-Shot', loop: 'Loop' };

/**
 * A Sound's key: Any, then the twelve notes as a grid, one radio button each (the arrow keys move
 * between them, Space chooses). Choosing a key chooses no scale.
 */
function KeyControl({
  field,
  value,
  onChange,
  readOnly,
}: {
  field: CreateField;
  value: string;
  onChange: (key: string) => void;
  readOnly: boolean;
}) {
  const id = useId();
  const notes = (field.values ?? []).filter((key) => key !== ANY_KEY);
  const chip = (key: string) => (
    <Chip key={key} value={key} name={id} disabled={readOnly} size="sm" radius="sm">
      {key === ANY_KEY ? 'Any' : key}
    </Chip>
  );
  return (
    <Stack gap={6}>
      <Text id={id} size="sm" fw={500}>
        {field.label}
      </Text>
      <Chip.Group
        multiple={false}
        value={value}
        onChange={(chosen) => {
          if (!readOnly) {
            onChange(chosen);
          }
        }}
      >
        <Stack
          gap={6}
          role="radiogroup"
          aria-labelledby={id}
          data-option={field.option ?? undefined}
        >
          <Group gap={6}>{chip(ANY_KEY)}</Group>
          <SimpleGrid cols={{ base: 4, xs: 6 }} spacing={6} verticalSpacing={6}>
            {notes.map(chip)}
          </SimpleGrid>
        </Stack>
      </Chip.Group>
    </Stack>
  );
}

/**
 * Every option a Sound can be created with, as Suno's Sounds tab has them: a model of its own (never
 * a Song's), the description, the Type (One-Shot or Loop), BPM (empty for Auto), and the Key, with
 * a Major or Minor scale offered once a key other than Any is chosen. A Sound has no Simple or
 * Advanced form, and no lyrics or styles. A scale chosen and then hidden by going back to Any is
 * kept. Every limit, list, and explanation is the inventory's; `readOnly` (a frozen Version) lets
 * nothing change.
 */
export function SoundOptions(props: KindOptionsProps) {
  const { onOption, readOnly, kindControl } = props;
  const parts = optionParts(props);
  const { read, field } = parts;
  const bpm = field('soundBpm');
  const key = field(KEY_OPTION);
  const chosenKey = read.choice(KEY_OPTION) ?? ANY_KEY;

  return (
    <Stack gap="sm">
      <Group gap="lg" align="flex-start" wrap="wrap">
        {kindControl}
      </Group>
      {parts.model('soundsModel')}
      {parts.text('soundDescription', true)}
      {parts.choice('soundType', { labels: TYPE_LABELS })}
      {bpm && (
        <NumberControl
          field={bpm}
          value={read.optionalNumber('soundBpm')}
          onChange={(value) => {
            onOption('soundBpm', value);
          }}
          readOnly={readOnly}
          emptyLabel="Auto"
        />
      )}
      {key && (
        <KeyControl
          field={key}
          value={chosenKey}
          onChange={(value) => {
            onOption(KEY_OPTION, value);
          }}
          readOnly={readOnly}
        />
      )}
      {chosenKey !== ANY_KEY && parts.choice(SCALE_OPTION, { noneLabel: 'None' })}
    </Stack>
  );
}
