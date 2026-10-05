import { Group, Stack } from '@mantine/core';
import type { CreateField } from '../../api/createFields';
import { SPEECH_MODE_OPTION } from '../../api/versions';
import { ChoiceControl } from './OptionControls';
import { optionParts, type KindOptionsProps } from './optionParts';

/** A Speech's two forms, kept apart from a Song's. */
const MODE_FIELD: CreateField = {
  key: SPEECH_MODE_OPTION,
  label: 'Mode',
  tab: 'speech',
  modes: [],
  type: 'choice',
  values: ['simple', 'advanced'],
  option: SPEECH_MODE_OPTION,
  help: null,
};

/**
 * Every option a Speech can be created with, as Suno's Speech tab has them. There is no model (Suno
 * offers none for Speech) and no lyrics or styles. Simple: one description. Advanced: the Script,
 * the Tone, then Vocal Gender, Background music, and Variety, which are a Speech's own and never a
 * Song's of the same name. Switching the form keeps every value. Every limit, list, and explanation
 * is the inventory's; `readOnly` (a frozen Version) lets nothing change.
 */
export function SpeechOptions(props: KindOptionsProps) {
  const { onOption, readOnly, kindControl } = props;
  const parts = optionParts(props);
  const mode = parts.read.choice(SPEECH_MODE_OPTION) ?? 'advanced';

  return (
    <Stack gap="sm">
      <Group gap="lg" align="flex-start" wrap="wrap">
        {kindControl}
        <ChoiceControl
          field={MODE_FIELD}
          value={mode}
          onChange={(value) => {
            if (value !== null) {
              onOption(SPEECH_MODE_OPTION, value);
            }
          }}
          readOnly={readOnly}
        />
      </Group>
      {mode === 'simple' ? (
        parts.text('speechPrompt', true)
      ) : (
        <>
          {parts.text('speechScript', true)}
          {parts.text('speechTone', true)}
          {parts.choice('speechVocalGender', { noneLabel: 'None' })}
          {parts.toggle('speechBackgroundMusic')}
          {parts.steps('speechVariety')}
        </>
      )}
    </Stack>
  );
}
