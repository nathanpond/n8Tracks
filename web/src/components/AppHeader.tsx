import { AppShell, Group, Title } from '@mantine/core';
import { ColorSchemeControl } from './ColorSchemeControl';
import { UserMenu } from './UserMenu';

/** The header every page has: the product name, the colour control, and the user menu when signed in. */
export function AppHeader() {
  return (
    <AppShell.Header>
      <Group h="100%" px="md" justify="space-between" wrap="nowrap">
        <Title order={1} size="h3">
          n8Tracks
        </Title>
        <Group gap="sm" wrap="nowrap">
          <ColorSchemeControl />
          <UserMenu />
        </Group>
      </Group>
    </AppShell.Header>
  );
}
