import { AppShell, Group, Title } from '@mantine/core';
import { ColorSchemeControl } from './ColorSchemeControl';

/** The header every page has: the product name and the colour control. */
export function AppHeader() {
  return (
    <AppShell.Header>
      <Group h="100%" px="md" justify="space-between" wrap="nowrap">
        <Title order={1} size="h3">
          n8Tracks
        </Title>
        <ColorSchemeControl />
      </Group>
    </AppShell.Header>
  );
}
