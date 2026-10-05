import { AppShell, Container, MantineProvider } from '@mantine/core';
import { Route, Routes } from 'react-router';
import { AppHeader } from './components/AppHeader';
import { HealthPanel } from './components/HealthPanel';
import { SetupGate } from './setup/SetupGate';
import { colorSchemeManager, cssVariablesResolver, theme } from './theme/theme';

function Shell() {
  return (
    <AppShell header={{ height: 56 }} padding="md">
      <AppHeader />
      <AppShell.Main>
        <Container size="sm" px={0}>
          <HealthPanel />
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}

/** The application: providers, the setup gate, and routes. It must be rendered inside a router. */
export function App() {
  return (
    <MantineProvider
      theme={theme}
      defaultColorScheme="auto"
      colorSchemeManager={colorSchemeManager}
      cssVariablesResolver={cssVariablesResolver}
    >
      <SetupGate>
        <Routes>
          {/* One catch-all route until real navigation arrives. */}
          <Route path="*" element={<Shell />} />
        </Routes>
      </SetupGate>
    </MantineProvider>
  );
}
