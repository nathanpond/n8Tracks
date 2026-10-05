import { MantineProvider } from '@mantine/core';
import { Navigate, Route, Routes } from 'react-router';
import { SessionGate } from './auth/SessionGate';
import { AccountPage } from './settings/AccountPage';
import { CredentialsPage } from './settings/CredentialsPage';
import { SystemPage } from './settings/SystemPage';
import { SetupGate } from './setup/SetupGate';
import { SignedInShell } from './shell/AppShell';
import { NotFoundPage } from './shell/NotFoundPage';
import { SongPage } from './songs/SongPage';
import { SongsPage } from './songs/SongsPage';
import { colorSchemeManager, cssVariablesResolver, theme } from './theme/theme';

/** The application: providers, the setup and session gates, and routes. It must be rendered inside a router. */
export function App() {
  return (
    <MantineProvider
      theme={theme}
      defaultColorScheme="auto"
      colorSchemeManager={colorSchemeManager}
      cssVariablesResolver={cssVariablesResolver}
    >
      <SetupGate>
        <SessionGate>
          <Routes>
            <Route element={<SignedInShell />}>
              <Route index element={<Navigate to="/songs" replace />} />
              <Route path="songs" element={<SongsPage />} />
              <Route path="songs/:reference" element={<SongPage />} />
              <Route path="songs/:reference/v/:number" element={<SongPage />} />
              <Route path="settings" element={<Navigate to="/settings/account" replace />} />
              <Route path="settings/account" element={<AccountPage />} />
              <Route path="settings/credentials" element={<CredentialsPage />} />
              <Route path="settings/system" element={<SystemPage />} />
              <Route path="*" element={<NotFoundPage />} />
            </Route>
          </Routes>
        </SessionGate>
      </SetupGate>
    </MantineProvider>
  );
}
