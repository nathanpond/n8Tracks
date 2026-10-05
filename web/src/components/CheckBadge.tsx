import { Badge } from '@mantine/core';

/**
 * The result of a check as a word on a coloured badge, in the status colours: `healthy` for a pass,
 * `degraded` for a warning, `unhealthy` for a failure.
 */
export function CheckBadge({
  tone,
  children,
}: {
  tone: 'healthy' | 'degraded' | 'unhealthy';
  children: string;
}) {
  return (
    <Badge
      variant="filled"
      size="lg"
      radius="sm"
      tt="none"
      data-tone={tone}
      bg={`var(--n8-status-${tone}-background)`}
      c={`var(--n8-status-${tone}-text)`}
    >
      {children}
    </Badge>
  );
}
