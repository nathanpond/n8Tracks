import { Button, Group, Stack, Text, Title } from '@mantine/core';
import { useId, useState, type ReactNode } from 'react';
import {
  fieldOf,
  type CreateField,
  type CreateFields,
  type OptionValue,
} from '../../api/createFields';
import { KIND_OPTION, SONG_MODE_OPTION, type VersionOptions } from '../../api/versions';
import {
  ChoiceControl,
  ModelControl,
  RangeControl,
  StepsControl,
  TextControl,
  ToggleControl,
} from './OptionControls';

/** Which of the Version's own text fields to show, as the form in use has them. */
export interface TextSections {
  lyrics: boolean;
  styles: boolean;
}

/** The kinds a Version can create, as Suno's tabs; only Song can be chosen until the Speech and Sound story. */
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

/** A Song's two forms. */
const MODE_FIELD: CreateField = {
  key: SONG_MODE_OPTION,
  label: 'Mode',
  tab: 'songs',
  modes: [],
  type: 'choice',
  values: ['simple', 'advanced'],
  option: SONG_MODE_OPTION,
  help: null,
};

const NOT_YET = ['speech', 'sound'];

/** An option's value read as text, a number, or on/off; the field's default when it holds another type. */
function reader(options: VersionOptions) {
  return {
    text: (key: string): string => {
      const value = options[key];
      return typeof value === 'string' ? value : '';
    },
    choice: (key: string): string | null => {
      const value = options[key];
      return typeof value === 'string' ? value : null;
    },
    number: (key: string, field: CreateField): number => {
      const value = options[key];
      return typeof value === 'number'
        ? value
        : typeof field.default === 'number'
          ? field.default
          : 0;
    },
    flag: (key: string): boolean => options[key] === true,
  };
}

/**
 * Every option a Song can be created with, laid out as Suno's Create screen lays them out, in
 * either of its two forms. The kind selector (only Song can be chosen yet) and, for a Song, the
 * Simple or Advanced switch come first, then the model.
 *
 * Advanced: the lyrics and styles (`text`), then More Options, collapsed as Suno has it (Exclude
 * styles, Vocal Gender, Duration, Max Mode, Weirdness, Style Influence, Variety, Personalize), and
 * Suno's title outside it. Simple: one prompt, and the lyrics and styles each only when their
 * section is added; removing a section hides it and keeps its text, which is the same text Advanced
 * shows. Switching the form hides what does not apply and keeps every value, so switching back
 * shows them again. Every limit, list, and explanation is the inventory's (`fields`). `readOnly`
 * (a frozen Version) shows every value but lets none change.
 */
export function SongOptions({
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
  const read = reader(options);
  const [moreOpen, setMoreOpen] = useState(false);
  const moreId = useId();
  const kind = read.choice(KIND_OPTION) ?? 'song';
  const mode = read.choice(SONG_MODE_OPTION) ?? 'advanced';
  const field = (option: string) => fieldOf(fields, option);

  const textOption = (option: string, multiline = false) => {
    const found = field(option);
    return (
      found && (
        <TextControl
          field={found}
          value={read.text(option)}
          onChange={(value) => {
            onOption(option, value);
          }}
          readOnly={readOnly}
          multiline={multiline}
        />
      )
    );
  };

  const rangeOption = (option: string) => {
    const found = field(option);
    return (
      found && (
        <RangeControl
          field={found}
          value={read.number(option, found)}
          onChange={(value) => {
            onOption(option, value);
          }}
          readOnly={readOnly}
        />
      )
    );
  };

  const toggleOption = (option: string) => {
    const found = field(option);
    return (
      found && (
        <ToggleControl
          field={found}
          value={read.flag(option)}
          onChange={(value) => {
            onOption(option, value);
          }}
          readOnly={readOnly}
        />
      )
    );
  };

  const model = field('model');
  const vocalGender = field('vocalGender');
  const durationMode = field('durationMode');
  const durationSeconds = field('durationSeconds');
  const variety = field('variety');

  const header = (
    <Group gap="lg" align="flex-start" wrap="wrap">
      <ChoiceControl
        field={KIND_FIELD}
        value={kind}
        onChange={(value) => {
          if (value !== null) {
            onOption(KIND_OPTION, value);
          }
        }}
        readOnly={readOnly}
        disabledValues={NOT_YET}
      />
      {kind === 'song' && (
        <ChoiceControl
          field={MODE_FIELD}
          value={mode}
          onChange={(value) => {
            if (value !== null) {
              onOption(SONG_MODE_OPTION, value);
            }
          }}
          readOnly={readOnly}
        />
      )}
    </Group>
  );

  if (kind !== 'song') {
    return (
      <Stack gap="sm">
        <Title order={4} size="h6">
          Options
        </Title>
        {header}
        <Text size="sm">
          This Version is a {kind}. n8Tracks does not show the options of a {kind} yet; its lyrics
          and styles are below.
        </Text>
        {text({ lyrics: true, styles: true })}
      </Stack>
    );
  }

  const simple = mode === 'simple';
  const lyricsAdded = read.flag('simpleLyricsAdded');
  const stylesAdded = read.flag('simpleStylesAdded');

  return (
    <Stack gap="sm">
      <Title order={4} size="h6">
        Options
      </Title>
      {header}
      {model && (
        <ModelControl
          field={model}
          models={fields.models}
          value={read.choice('model')}
          onChange={(value) => {
            onOption('model', value);
          }}
          readOnly={readOnly}
        />
      )}
      {simple ? (
        <>
          {textOption('simplePrompt', true)}
          {(lyricsAdded || stylesAdded) && text({ lyrics: lyricsAdded, styles: stylesAdded })}
          {!readOnly && (
            <Group gap="sm">
              <Button
                variant="default"
                size="compact-sm"
                onClick={() => {
                  onOption('simpleLyricsAdded', !lyricsAdded);
                }}
              >
                {lyricsAdded ? 'Remove lyrics' : 'Add lyrics'}
              </Button>
              <Button
                variant="default"
                size="compact-sm"
                onClick={() => {
                  onOption('simpleStylesAdded', !stylesAdded);
                }}
              >
                {stylesAdded ? 'Remove styles' : 'Add styles'}
              </Button>
            </Group>
          )}
          {(lyricsAdded || stylesAdded) && (
            <Text size="xs" c="var(--n8-color-secondary-text)">
              Removing a section hides it; its text is kept, and Advanced mode shows it.
            </Text>
          )}
        </>
      ) : (
        <>
          {text({ lyrics: true, styles: true })}
          <div>
            <Button
              variant="subtle"
              size="compact-sm"
              aria-expanded={moreOpen}
              aria-controls={moreId}
              onClick={() => {
                setMoreOpen((open) => !open);
              }}
            >
              <span aria-hidden="true">{moreOpen ? '▾' : '▸'}</span>&nbsp;More Options
            </Button>
          </div>
          {moreOpen && (
            <Stack gap="md" id={moreId} pl="sm" data-testid="more-options">
              {textOption('excludeStyles')}
              {vocalGender && (
                <ChoiceControl
                  field={vocalGender}
                  value={read.choice('vocalGender')}
                  onChange={(value) => {
                    onOption('vocalGender', value);
                  }}
                  readOnly={readOnly}
                  noneLabel="None"
                />
              )}
              {durationMode && (
                <ChoiceControl
                  field={durationMode}
                  value={read.choice('durationMode')}
                  onChange={(value) => {
                    if (value !== null) {
                      onOption('durationMode', value);
                    }
                  }}
                  readOnly={readOnly}
                />
              )}
              {durationSeconds && read.choice('durationMode') === 'custom' && (
                <RangeControl
                  field={durationSeconds}
                  label="Custom duration"
                  value={read.number('durationSeconds', durationSeconds)}
                  onChange={(value) => {
                    onOption('durationSeconds', value);
                  }}
                  readOnly={readOnly}
                />
              )}
              {toggleOption('maxMode')}
              {rangeOption('weirdness')}
              {rangeOption('styleInfluence')}
              {variety && (
                <StepsControl
                  field={variety}
                  value={read.text('variety')}
                  onChange={(value) => {
                    onOption('variety', value);
                  }}
                  readOnly={readOnly}
                />
              )}
              {toggleOption('personalize')}
            </Stack>
          )}
          {textOption('title')}
        </>
      )}
    </Stack>
  );
}
