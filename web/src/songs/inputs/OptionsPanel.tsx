import { Stack, Title } from '@mantine/core';
import type { ReactNode } from 'react';
import type { CreateField, CreateFields, OptionValue } from '../../api/createFields';
import { isVersionKind, KIND_OPTION, type VersionOptions } from '../../api/versions';
import { ChoiceControl } from './OptionControls';
import { SongOptions, type TextSections } from './SongOptions';
import { SoundOptions } from './SoundOptions';
import { SpeechOptions } from './SpeechOptions';

/** The kinds a Version can create, as Suno's tabs. */
const KIND_FIELD: CreateField = {
  key: KIND_OPTION,
  label: 'Kind',
  tab: '',
  modes: [],
  type: 'choice',
  values: ['song', 'speech', 'sound'],
  option: KIND_OPTION,
  help: null,
};

/**
 * A Version's options: the kind selector, then one component per kind, chosen from the Version's
 * kind (Song, Speech, or Sound), each laying its options out as Suno's tab for it does. Switching
 * the kind replaces the options shown; every value of every kind stays stored with the Version, so
 * switching back finds them. The lyrics and styles (`text`) belong to a Song only.
 */
export function OptionsPanel({
  fields,
  options,
  onOption,
  readOnly,
  text,
}: {
  fields: CreateFields;
  options: VersionOptions;
  onOption: (key: string, value: OptionValue) => void;
  readOnly: boolean;
  /** The lyrics and styles inputs, showing the sections given. */
  text: (sections: TextSections) => ReactNode;
}) {
  const stored = options[KIND_OPTION];
  const kind = isVersionKind(stored) ? stored : 'song';
  const kindControl = (
    <ChoiceControl
      field={KIND_FIELD}
      value={kind}
      onChange={(value) => {
        if (value !== null) {
          onOption(KIND_OPTION, value);
        }
      }}
      readOnly={readOnly}
    />
  );
  const props = { fields, options, onOption, readOnly, kindControl };

  return (
    <Stack gap="sm">
      <Title order={4} size="h6">
        Options
      </Title>
      {kind === 'speech' ? (
        <SpeechOptions {...props} />
      ) : kind === 'sound' ? (
        <SoundOptions {...props} />
      ) : (
        <SongOptions {...props} text={text} />
      )}
    </Stack>
  );
}
