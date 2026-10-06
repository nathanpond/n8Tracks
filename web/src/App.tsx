import { MantineProvider } from '@mantine/core';
import { Navigate, Route, Routes } from 'react-router';
import { SessionGate } from './auth/SessionGate';
import { AlbumPage } from './albums/AlbumPage';
import { AlbumsPage } from './albums/AlbumsPage';
import { ArtistPage } from './artists/ArtistPage';
import { ArtistsPage } from './artists/ArtistsPage';
import { MaintenanceGate } from './maintenance/MaintenanceGate';
import { PlaylistPage } from './playlists/PlaylistPage';
import { PlaylistsPage } from './playlists/PlaylistsPage';
import { AccountPage } from './settings/AccountPage';
import { BackupsPage } from './settings/BackupsPage';
import { CatalogPage } from './settings/CatalogPage';
import { CredentialsPage } from './settings/CredentialsPage';
import { GenresPage } from './settings/GenresPage';
import { SunoPage } from './settings/SunoPage';
import { TagsPage } from './settings/TagsPage';
import { SystemPage } from './settings/SystemPage';
import { WorkflowPage } from './settings/WorkflowPage';
import { SetupGate } from './setup/SetupGate';
import { SignedInShell } from './shell/AppShell';
import { GoPage } from './shell/GoPage';
import { NotFoundPage } from './shell/NotFoundPage';
import { SongPage } from './songs/SongPage';
import { SongsPage } from './songs/SongsPage';
import { colorSchemeManager, cssVariablesResolver, theme } from './theme/theme';

/**
 * The application: providers, the maintenance, setup, and session gates, and routes. It must be
 * rendered inside a router.
 */
export function App() {
  return (
    <MantineProvider
      theme={theme}
      defaultColorScheme="auto"
      colorSchemeManager={colorSchemeManager}
      cssVariablesResolver={cssVariablesResolver}
    >
      <MaintenanceGate>
        <SetupGate>
          <SessionGate>
            <Routes>
              <Route element={<SignedInShell />}>
                <Route index element={<Navigate to="/songs" replace />} />
                <Route path="songs" element={<SongsPage />} />
                <Route path="songs/:reference" element={<SongPage />} />
                <Route path="songs/:reference/v/:number" element={<SongPage />} />
                <Route path="artists" element={<ArtistsPage />} />
                <Route path="artists/:id" element={<ArtistPage />} />
                <Route path="albums" element={<AlbumsPage />} />
                <Route path="albums/:id" element={<AlbumPage />} />
                <Route path="playlists" element={<PlaylistsPage />} />
                <Route path="playlists/:id" element={<PlaylistPage />} />
                <Route path="go/:reference" element={<GoPage />} />
                <Route path="settings" element={<Navigate to="/settings/account" replace />} />
                <Route path="settings/account" element={<AccountPage />} />
                <Route path="settings/credentials" element={<CredentialsPage />} />
                <Route path="settings/workflow" element={<WorkflowPage />} />
                <Route path="settings/catalog" element={<CatalogPage />} />
                <Route path="settings/genres" element={<GenresPage />} />
                <Route path="settings/tags" element={<TagsPage />} />
                <Route path="settings/suno" element={<SunoPage />} />
                <Route path="settings/backups" element={<BackupsPage />} />
                <Route path="settings/system" element={<SystemPage />} />
                <Route path="*" element={<NotFoundPage />} />
              </Route>
            </Routes>
          </SessionGate>
        </SetupGate>
      </MaintenanceGate>
    </MantineProvider>
  );
}
