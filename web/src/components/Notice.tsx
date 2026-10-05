import { Paper, Stack, Text } from '@mantine/core';
import type { ReactNode } from 'react';

/** A boxed message in the notice colours, for a warning or a failure the user should read. */
export function Notice({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Paper
      p="sm"
      bg="var(--n8-notice-background)"
      c="var(--n8-notice-text)"
      style={{ border: '1px solid var(--n8-notice-border)' }}
    >
      <Stack gap={4}>
        <Text fw={700}>{title}</Text>
        {children}
      </Stack>
    </Paper>
  );
}
