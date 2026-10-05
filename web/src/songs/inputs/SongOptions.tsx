import { Button, Group, Stack, Text } from '@mantine/core';
import { useId, useState, type ReactNode } from 'react';
import type { CreateField } from '../../api/createFields';
import { SONG_MODE_OPTION } from '../../api/versions';
import { ChoiceControl, RangeControl } from './OptionControls';
import { optionParts, type KindOptionsProps } from './optionParts';

/** Which of the Version's own text fields to show, as the form in use has them. */
export interface TextSections {
  lyrics: boolean;
  styles: boolean;
}

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

/**
 * Every option a Song can be created with, laid out as Suno's Create screen lays them out, in
 * either of its two forms. The kind selector and the Simple or Advanced switch come first, then
 * the model.
 *
 * Advanced: the lyrics and styles (`text`), then More Options, collapsed as Suno has it (Exclude
 * styles, Vocal Gender, Duration, Max Mode, Weirdness, Style Influence, Variety, Personalize), and
 * Suno's title outside it. Simple: one prompt, and the lyrics and styles each only when their
 * section is added; removing a section hides it and keeps its text, which is the same text Advanced
 * shows. Switching the form hides what does not apply and keeps every value, so switching back
 * shows them again. Every limit, list, and explanation is the inventory's (`fields`). `readOnly`
 * (a frozen Version) shows every value but lets none change.
 */
export function SongOptions(
  props: KindOptionsProps & {
    /** The lyrics and styles inputs, showing the sections given. */
    text: (sections: TextSections) => ReactNode;
  },
) {
  const { onOption, readOnly, text, kindControl } = props;
  const parts = optionParts(props);
  const { read, field } = parts;
  const [moreOpen, setMoreOpen] = useState(false);
  const moreId = useId();
  const mode = read.choice(SONG_MODE_OPTION) ?? 'advanced';

  const textOption = parts.text;
  const rangeOption = parts.range;
  const toggleOption = parts.toggle;

  const model = field('model');
  const vocalGender = field('vocalGender');
  const durationMode = field('durationMode');
  const durationSeconds = field('durationSeconds');
  const variety = field('variety');

  const header = (
    <Group gap="lg" align="flex-start" wrap="wrap">
      {kindControl}
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
    </Group>
  );

  const simple = mode === 'simple';
  const lyricsAdded = read.flag('simpleLyricsAdded');
  const stylesAdded = read.flag('simpleStylesAdded');

  return (
    <Stack gap="sm">
      {header}
      {model && parts.model('model')}
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
              {vocalGender && parts.choice('vocalGender', { noneLabel: 'None' })}
              {durationMode && parts.choice('durationMode')}
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
              {variety && parts.steps('variety')}
              {toggleOption('personalize')}
            </Stack>
          )}
          {textOption('title')}
        </>
      )}
    </Stack>
  );
}
