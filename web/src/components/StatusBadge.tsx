import { Badge, Text } from '@mantine/core';
import { isKnownStatus } from '../theme/palette';

/**
 * A status as text on a coloured badge. A status this build does not know is plain text: there is
 * no colour that would mean anything for it.
 */
export function StatusBadge({ status }: { status: string }) {
  if (!isKnownStatus(status)) {
    return <Text span>{status}</Text>;
  }

  return (
    <Badge
      variant="filled"
      size="lg"
      radius="sm"
      tt="none"
      data-status={status}
      bg={`var(--n8-status-${status}-background)`}
      c={`var(--n8-status-${status}-text)`}
    >
      {status}
    </Badge>
  );
}
