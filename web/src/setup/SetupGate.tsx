import { AppShell, Button, Container, Group, Loader, Stack, Text } from '@mantine/core';
import type { ReactNode } from 'react';
import { Navigate, Route, Routes } from 'react-router';
import { useSetupStatus } from '../api/setup';
import { AppHeader } from '../components/AppHeader';
import { SetupWizard } from './SetupWizard';

/** The route of the setup wizard, relative to the app's base path. */
export const SETUP_ROUTE = 'setup';

function Page({ children }: { children: ReactNode }) {
  return (
    <AppShell header={{ height: 56 }} padding="md">
      <AppHeader />
      <AppShell.Main>
        <Container size="sm" px={0}>
          {children}
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}

/**
 * Asks the backend whether setup is complete before any route renders. Until it is, every page
 * redirects to the setup wizard; once it is, the wizard redirects to the app and `children` (the
 * app's routes) render.
 */
export function SetupGate({ children }: { children: ReactNode }) {
  const { state, refresh, markComplete } = useSetupStatus();

  if (state.phase === 'loading') {
    return (
      <Page>
        <Group gap="sm" aria-live="polite">
          <Loader size="sm" aria-hidden="true" />
          <Text>Loading…</Text>
        </Group>
      </Page>
    );
  }

  if (state.phase === 'error') {
    return (
      <Page>
        <Stack gap="sm" align="flex-start" aria-live="polite">
          <Text>n8Tracks is not answering. Check that it is running, then retry.</Text>
          <Button variant="default" onClick={refresh}>
            Retry
          </Button>
        </Stack>
      </Page>
    );
  }

  const { status, checking } = state;
  if (status.complete) {
    return (
      <Routes>
        <Route path={SETUP_ROUTE} element={<Navigate to="/" replace />} />
        <Route path="*" element={children} />
      </Routes>
    );
  }

  return (
    <Routes>
      <Route
        path={SETUP_ROUTE}
        element={
          <Page>
            <SetupWizard
              status={status}
              checking={checking}
              onRecheck={refresh}
              onComplete={markComplete}
            />
          </Page>
        }
      />
      <Route path="*" element={<Navigate to={`/${SETUP_ROUTE}`} replace />} />
    </Routes>
  );
}
