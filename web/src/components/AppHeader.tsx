import { AppShell, Burger, Group, Title } from '@mantine/core';
import type { ReactNode } from 'react';
import { ColorSchemeControl } from './ColorSchemeControl';

/** The sidebar toggle, shown on narrow screens where the sidebar is hidden until opened. */
export interface HeaderNavigation {
  opened: boolean;
  toggle: () => void;
}

/**
 * The header every page has: the product name and the colour control. The signed-in shell adds the
 * sidebar toggle (`navigation`) and the user menu (`children`).
 */
export function AppHeader({
  navigation,
  children,
}: {
  navigation?: HeaderNavigation;
  children?: ReactNode;
}) {
  return (
    <AppShell.Header>
      <Group h="100%" px="md" justify="space-between" wrap="nowrap">
        <Group gap="sm" wrap="nowrap">
          {navigation && (
            <Burger
              opened={navigation.opened}
              onClick={navigation.toggle}
              hiddenFrom="sm"
              size="sm"
              aria-label={navigation.opened ? 'Close navigation' : 'Open navigation'}
              aria-expanded={navigation.opened}
              aria-controls="app-navigation"
            />
          )}
          <Title order={1} size="h3">
            n8Tracks
          </Title>
        </Group>
        <Group gap="sm" wrap="nowrap">
          <ColorSchemeControl />
          {children}
        </Group>
      </Group>
    </AppShell.Header>
  );
}
