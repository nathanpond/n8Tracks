import { Badge, Tooltip, VisuallyHidden } from '@mantine/core';
import type { CSSProperties, ReactNode } from 'react';
import { formatDateTime, formatRelativeTime } from '../api/timeZone';
import { isStateColour } from '../theme/palette';

/**
 * A workflow state: its name in its colour, in the scheme in use. A colour this build does not
 * know is drawn in the body text colour.
 */
export function StateBadge({ name, colour }: { name: string; colour: string }) {
  const value = isStateColour(colour) ? `var(--n8-state-${colour})` : 'var(--mantine-color-text)';
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
 * Text with more to it shown on hover and on keyboard focus: the target is in the tab order so a
 * keyboard user can reach what a pointer user sees.
 */
function WithDetail({
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
