import { AppShell, Container } from '@mantine/core';
import type { ReactNode } from 'react';
import { AppHeader } from './AppHeader';

/** A page with the header and one narrow column: setup, sign-in, and the states before either. */
export function PlainPage({ children }: { children: ReactNode }) {
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
