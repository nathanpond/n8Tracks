import { Anchor, Divider, Drawer, Group, Loader, Stack, Text } from '@mantine/core';
import type { ReactNode } from 'react';
import { Link } from 'react-router';
import { generationDuration, ratingText, reportedModel, type Generation } from '../api/generations';
import { formatDateTime, useConfiguredTimeZone } from '../api/timeZone';
import { ShortcodeBadge } from '../common/ShortcodeBadge';
import { GenerationComments, type UpdateGeneration } from './GenerationComments';
import { GenerationStateBadges, OpenInSuno, SunoCreated } from './GenerationParts';
import { StarRating } from './StarRating';

/** What the panel shows: a Generation, one still loading, or one that cannot be found. */
export type GenerationPanelContent =
  | { kind: 'loading' }
  | { kind: 'failed' }
  | { kind: 'not-found'; reference: string }
  | { kind: 'found'; generation: Generation; versionLink: string; versionNumber: string };

/** A label and its value, as one row of the panel's description list. */
function Detail({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <Text component="dt" size="xs" fw={700} c="var(--n8-color-secondary-text)">
        {label}
      </Text>
      <Text component="dd" size="sm" m={0} style={{ overflowWrap: 'anywhere' }}>
        {children}
      </Text>
    </div>
  );
}

function Details({
  generation,
  versionLink,
  versionNumber,
  onRate,
  update,
}: {
  generation: Generation;
  versionLink: string;
  versionNumber: string;
  onRate: (generation: Generation, rating: number | null) => void;
  update: UpdateGeneration;
}) {
  const timeZone = useConfiguredTimeZone();
  return (
    <Stack gap="md">
      <ShortcodeBadge shortcode={generation.shortcode} testId="generation-panel-shortcode" />
      <Stack component="dl" gap="sm" m={0}>
        <Detail label="Suno title">{generation.title ?? 'Untitled'}</Detail>
        <Detail label="Version">
          <Anchor component={Link} to={versionLink} underline="always">
            Version {versionNumber}
          </Anchor>
        </Detail>
        <Detail label="Duration">{generationDuration(generation)}</Detail>
        <Detail label="Model">{reportedModel(generation) ?? 'Unknown'}</Detail>
        <Detail label="Rating">
          <Group gap="sm" wrap="wrap">
            <StarRating
              value={generation.rating}
              label={`Rating of ${generation.shortcode}`}
              onChange={(rating) => {
                onRate(generation, rating);
              }}
            />
            <span data-testid="generation-panel-rating">{ratingText(generation.rating)}</span>
          </Group>
        </Detail>
        <Detail label="State">
          <GenerationStateBadges generation={generation} />
        </Detail>
        <Detail label="Created in Suno">
          <SunoCreated generation={generation} timeZone={timeZone} />
        </Detail>
        <Detail label="Added to n8Tracks">
          <time dateTime={generation.createdAt}>
            {formatDateTime(generation.createdAt, timeZone)}
          </time>
        </Detail>
      </Stack>
      <div>
        <OpenInSuno generation={generation} />
      </div>
      <Divider />
      <GenerationComments generation={generation} update={update} />
    </Stack>
  );
}

/**
 * A Generation's details, beside the Versions table as a drawer with its own address
 * (`/songs/<song>/generations/<shortcode>`): its shortcode with a one-click copy, what Suno reported
 * (title, length or "Generating", model, when Suno made it), the user's star rating (set, changed,
 * or cleared here, `onRate`), its state with the Song's Selected marker, Suno's page for it, and the
 * user's comments (`update` keeps the Song's one cached copy in step). `problem` says why a rating
 * was not saved. The archive, select, artwork,
 * move, and delete controls arrive with their own stories. A reference that names no Generation of
 * this Song says so. Closes with Escape or its close control.
 */
export function GenerationPanel({
  opened,
  content,
  onClose,
  onRate,
  update,
  problem,
}: {
  opened: boolean;
  content: GenerationPanelContent;
  onClose: () => void;
  onRate: (generation: Generation, rating: number | null) => void;
  update: UpdateGeneration;
  problem?: string | undefined;
}) {
  const title =
    content.kind === 'found'
      ? `Generation ${content.generation.shortcode}`
      : content.kind === 'not-found'
        ? 'Generation not found'
        : 'Generation';
  return (
    <Drawer
      opened={opened}
      onClose={onClose}
      position="right"
      size="md"
      title={title}
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      {content.kind === 'loading' && <Loader aria-label="Loading the Generation" />}
      {content.kind === 'failed' && (
        <Text role="alert">
          The Generation could not be loaded: n8Tracks did not answer as expected. Check that it is
          running and try again.
        </Text>
      )}
      {content.kind === 'not-found' && (
        <Text data-testid="generation-not-found" style={{ overflowWrap: 'anywhere' }}>
          This Song has no Generation {content.reference}. It may have been deleted, or the
          shortcode may be mistyped.
        </Text>
      )}
      {content.kind === 'found' && problem !== undefined && (
        <Text role="alert" size="sm" c="var(--mantine-color-error)" mb="sm">
          {problem}
        </Text>
      )}
      {content.kind === 'found' && (
        <Details
          generation={content.generation}
          versionLink={content.versionLink}
          versionNumber={content.versionNumber}
          onRate={onRate}
          update={update}
        />
      )}
    </Drawer>
  );
}
