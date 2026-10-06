import { Badge, Group, Tooltip, VisuallyHidden } from '@mantine/core';
import type { CSSProperties, ReactNode } from 'react';
import { formatDateTime, formatRelativeTime } from '../api/timeZone';
import { paletteColour } from '../theme/palette';

/**
 * A workflow state: its name in its colour, in the scheme in use. A colour this build does not
 * know is drawn in the body text colour.
 */
export function StateBadge({ name, colour }: { name: string; colour: string }) {
  const value = paletteColour(colour);
  return (
    <Badge
      variant="outline"
      radius="sm"
      tt="none"
      data-state-colour={colour}
      color={value}
      c={value}
      style={{ borderColor: value }}
    >
      {name}
    </Badge>
  );
}

/**
 * A Tag: a coloured label, a swatch and an outline in its colour, with its name always written in
 * it, so the colour is never the only way to tell Tags apart. The colour pairs are the workflow
 * states' (each checked as text on the page in both schemes).
 */
export function TagLabel({ name, colour }: { name: string; colour: string }) {
  const value = paletteColour(colour);
  return (
    <Badge
      variant="outline"
      radius="xl"
      tt="none"
      fw={500}
      data-tag-colour={colour}
      color={value}
      c={value}
      style={{ borderColor: value, maxWidth: '100%' }}
      leftSection={
        <span
          aria-hidden="true"
          style={{
            display: 'inline-block',
            width: 8,
            height: 8,
            borderRadius: '50%',
            background: value,
          }}
        />
      }
    >
      {name}
    </Badge>
  );
}

/** The "+N" that stands for the Tags a row leaves out. */
const MORE_STYLE: CSSProperties = { fontSize: 'var(--mantine-font-size-xs)' };

/**
 * A Song's Tags as labels, in the order given. With `limit`, only the first `limit` are drawn and
 * a "+N" stands for the rest, which show (as labels) on hover and keyboard focus of it, and are
 * read out with it.
 */
export function TagLabels({
  tags,
  limit,
}: {
  tags: readonly { id: string; name: string; colour: string }[];
  limit?: number;
}) {
  const shown = limit === undefined ? tags : tags.slice(0, limit);
  const rest = tags.slice(shown.length);
  return (
    <Group gap={4} wrap="wrap" component="span" data-testid="tag-labels">
      {shown.map((tag) => (
        <TagLabel key={tag.id} name={tag.name} colour={tag.colour} />
      ))}
      {rest.length > 0 && (
        <Tooltip
          label={
            <Group gap={4} wrap="wrap" maw={320}>
              {rest.map((tag) => (
                <TagLabel key={tag.id} name={tag.name} colour={tag.colour} />
              ))}
            </Group>
          }
          events={{ hover: true, focus: true, touch: true }}
          // On the page's own background, so each label keeps the contrast checked for it.
          color="var(--mantine-color-body)"
          withArrow
          style={{ border: '1px solid var(--mantine-color-default-border)' }}
        >
          {/* eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- the hidden Tags must show on keyboard focus as well as hover (AC), the WAI tooltip pattern */}
          <span tabIndex={0} data-testid="more-tags" style={MORE_STYLE}>
            +{rest.length}
            <VisuallyHidden>
              {` more ${rest.length === 1 ? 'Tag' : 'Tags'}: ${rest.map((tag) => tag.name).join(', ')}`}
            </VisuallyHidden>
          </span>
        </Tooltip>
      )}
    </Group>
  );
}

/**
 * Text with more to it shown on hover and on keyboard focus: the target is in the tab order so a
 * keyboard user can reach what a pointer user sees.
 */
export function WithDetail({
  detail,
  style,
  children,
}: {
  detail: ReactNode;
  style?: CSSProperties;
  children: ReactNode;
}) {
  return (
    <Tooltip
      label={detail}
      events={{ hover: true, focus: true, touch: true }}
      multiline
      maw={420}
      style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}
    >
      {/* eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- the full text must show on keyboard focus as well as hover (AC), the WAI tooltip pattern */}
      <span tabIndex={0} style={style}>
        {children}
      </span>
    </Tooltip>
  );
}

/** A long concept on one line, cut off with an ellipsis; the full text on hover and focus. */
export function TruncatedConcept({ concept }: { concept: string }) {
  return (
    <WithDetail
      detail={concept}
      style={{
        display: 'block',
        overflow: 'hidden',
        textOverflow: 'ellipsis',
        whiteSpace: 'nowrap',
      }}
    >
      {concept.replace(/\s*\n\s*/g, ' ')}
    </WithDetail>
  );
}

/**
 * A time relative to now ("5 minutes ago"), with the date and time in the configured zone on hover
 * and focus, and for a screen reader.
 */
export function RelativeTime({ utc, timeZone }: { utc: string; timeZone: string }) {
  const absolute = formatDateTime(utc, timeZone);
  return (
    <WithDetail detail={absolute}>
      <time dateTime={utc}>{formatRelativeTime(utc)}</time>
      <VisuallyHidden>, {absolute}</VisuallyHidden>
    </WithDetail>
  );
}
