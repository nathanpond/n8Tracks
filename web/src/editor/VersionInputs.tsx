import { Stack, Textarea, Title } from '@mantine/core';
import { VERSION_LYRICS_MAXIMUM_LENGTH, VERSION_STYLES_MAXIMUM_LENGTH } from '../api/versions';
import { formatCount } from './counts';
import { LyricsEditor } from './LyricsEditor';

/** Styles as typed: a textarea's value already has `\n` line endings, and nothing else changes. */
function asTyped(text: string): string {
  return text.replace(/\r\n|\r/g, '\n');
}

/**
 * A Version's creation inputs: the lyrics editor and the Styles field, showing the drafts the
 * Version pane holds. There is no Save: the pane saves them automatically (`useAutosave`), with
 * the name and notes, and says whether they are stored. Text over a limit can be typed or pasted;
 * the counter and a message say so, and it is not sent until it is back under.
 */
export function VersionInputs({
  lyrics,
  styles,
  onLyrics,
  onStyles,
}: {
  lyrics: string;
  styles: string;
  onLyrics: (lyrics: string) => void;
  onStyles: (styles: string) => void;
}) {
  const stylesExcess = styles.length - VERSION_STYLES_MAXIMUM_LENGTH;

  return (
    <Stack gap="sm">
      <Title order={4} size="h6" id="version-inputs">
        Lyrics and styles
      </Title>
      <LyricsEditor
        value={lyrics}
        onChange={onLyrics}
        label="Lyrics"
        maximumLength={VERSION_LYRICS_MAXIMUM_LENGTH}
      />
      <Textarea
        label="Styles"
        description={`${formatCount(styles.length)} / ${formatCount(VERSION_STYLES_MAXIMUM_LENGTH)} characters`}
        rows={3}
        resize="vertical"
        value={styles}
        onChange={(event) => {
          onStyles(asTyped(event.currentTarget.value));
        }}
        error={
          stylesExcess > 0
            ? `Over the limit by ${formatCount(stylesExcess)} ${stylesExcess === 1 ? 'character' : 'characters'}. Shorten the text to save.`
            : undefined
        }
      />
    </Stack>
  );
}
