import { AppShell, Container, Group, MantineProvider, Title } from '@mantine/core';
import { Route, Routes } from 'react-router';
import { ColorSchemeControl } from './components/ColorSchemeControl';
import { HealthPanel } from './components/HealthPanel';
import { colorSchemeManager, cssVariablesResolver, theme } from './theme/theme';

function Shell() {
  return (
    <AppShell header={{ height: 56 }} padding="md">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between" wrap="nowrap">
          <Title order={1} size="h3">
            n8Tracks
          </Title>
          <ColorSchemeControl />
        </Group>
      </AppShell.Header>
      <AppShell.Main>
        <Container size="sm" px={0}>
          <HealthPanel />
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}

/** The application: providers and routes. It must be rendered inside a router. */
export function App() {
  return (
    <MantineProvider
      theme={theme}
      defaultColorScheme="auto"
      colorSchemeManager={colorSchemeManager}
      cssVariablesResolver={cssVariablesResolver}
    >
      <Routes>
        {/* One catch-all route until real navigation arrives. */}
        <Route path="*" element={<Shell />} />
      </Routes>
    </MantineProvider>
  );
}
