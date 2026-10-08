import { Badge } from '@mantine/core';
import { sourceOf, sourceText, type NowPlaying } from './playerRules';

/**
 * Where what is playing comes from (#221), always shown in the player bar: "Local file" with its
 * format for a file n8Tracks serves, or "Streaming from Suno" for a Generation played from Suno's
 * own address, in words.
 */
export function SourceBadge({ playing }: { playing: NowPlaying }) {
  const source = sourceOf(playing);
  return (
    <Badge
      variant="default"
      size="sm"
      radius="sm"
      tt="none"
      data-testid="player-source"
      data-source={source}
    >
      {sourceText(playing)}
    </Badge>
  );
}
