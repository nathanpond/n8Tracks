import { Anchor, Badge, Group, Text, VisuallyHidden } from '@mantine/core';
import { sunoSongUrl, type Generation } from '../api/generations';
import { formatDateTime } from '../api/timeZone';
import { WithDetail } from '../songs/SongParts';

/** What Remote Missing means, on hover and focus and for a screen reader. */
export const REMOTE_MISSING_DETAIL =
  'Suno no longer lists this clip: it was not found the last time n8Tracks looked.';

/**
 * A Generation's state as badges: Active or Archived (the user's own choice), Remote Missing when
 * Suno no longer lists the clip (with a tooltip saying so), and Selected on the Song's Selected
 * Generation.
 */
export function GenerationStateBadges({ generation }: { generation: Generation }) {
  return (
    <Group gap={4} wrap="wrap" data-testid="generation-state">
      <Badge size="sm" variant="default" radius="sm" tt="none">
        {generation.state === 'archived' ? 'Archived' : 'Active'}
      </Badge>
      {generation.remoteState === 'missing' && (
        <WithDetail detail={REMOTE_MISSING_DETAIL}>
          <Badge size="sm" variant="default" radius="sm" tt="none" component="span">
            Remote Missing
          </Badge>
          <VisuallyHidden>: {REMOTE_MISSING_DETAIL}</VisuallyHidden>
        </WithDetail>
      )}
      {generation.isSelected && (
        <Badge size="sm" variant="filled" radius="sm" tt="none" data-testid="selected-generation">
          Selected
        </Badge>
      )}
    </Group>
  );
}

/** When Suno made the clip, in the configured time zone; "Unknown" when Suno did not say. */
export function SunoCreated({
  generation,
  timeZone,
}: {
  generation: Generation;
  timeZone: string;
}) {
  return generation.sunoCreatedAt === null ? (
    <Text span size="sm">
      Unknown
    </Text>
  ) : (
    <time dateTime={generation.sunoCreatedAt}>
      {formatDateTime(generation.sunoCreatedAt, timeZone)}
    </time>
  );
}

/**
 * Suno's page for the clip, in a new tab; nothing for a Generation with no Suno ID. The visible
 * words are in the accessible name, which also says which Generation and that it opens a tab.
 */
export function OpenInSuno({ generation }: { generation: Generation }) {
  if (generation.sunoId === null) {
    return null;
  }
  return (
    <Anchor
      href={sunoSongUrl(generation.sunoId)}
      target="_blank"
      rel="noopener noreferrer"
      size="sm"
      underline="always"
      aria-label={`Open in Suno: ${generation.shortcode} (opens a new tab)`}
    >
      Open in Suno
    </Anchor>
  );
}
