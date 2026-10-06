import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { CatalogSettings } from '../api/catalogSettings';
import { testArtist } from '../test/artistServer';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';

const N8 = testArtist('n8', { id: '01a10e00-0000-7000-9000-000000000001' });
const GUEST = testArtist('Guest Singer', { id: '01a10e00-0000-7000-9000-000000000002' });

/** A fake n8Tracks with the catalog settings and two Artists; `server.writes` holds each PUT. */
function catalogServer(initial: CatalogSettings = { revision: 1, defaultArtist: null }) {
  const server = {
    settings: initial,
    writes: [] as { ifMatch: string | null; body: unknown }[],
    changeElsewhere(settings: CatalogSettings) {
      server.settings = settings;
    },
  };
  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/artists')) {
      return Promise.resolve(
        jsonResponse(200, { items: [GUEST, N8], page: 1, pageSize: 10, total: 2 }),
      );
    }
    if (path.endsWith('/api/v1/settings/catalog')) {
      if ((init?.method ?? 'GET') === 'GET') {
        return Promise.resolve(jsonResponse(200, server.settings));
      }
      const ifMatch = new Headers(init?.headers).get('If-Match');
      const body = JSON.parse(typeof init?.body === 'string' ? init.body : '{}') as {
        defaultArtistId: string | null;
      };
      server.writes.push({ ifMatch, body });
      if (ifMatch !== `"${String(server.settings.revision)}"`) {
        return Promise.resolve(
          jsonResponse(409, { code: 'revision_conflict', current: server.settings }),
        );
      }
      const artist = [N8, GUEST].find((candidate) => candidate.id === body.defaultArtistId);
      server.settings = {
        revision: server.settings.revision + 1,
        defaultArtist: artist === undefined ? null : { id: artist.id, name: artist.name },
      };
      return Promise.resolve(jsonResponse(200, server.settings));
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return server;
}

async function openCatalog() {
  renderApp('/settings/catalog');
  await screen.findByRole('heading', { level: 2, name: 'Catalog' });
  return screen.findByTestId('default-artist');
}

describe('Settings → Catalog', () => {
  it('chooses a default Artist and saves it on the settings’ revision', async () => {
    const server = catalogServer();
    const user = userEvent.setup();
    const current = await openCatalog();
    expect(current).toHaveTextContent('No default Artist.');

    await user.type(screen.getByRole('textbox', { name: 'Choose a default Artist' }), 'n');
    await user.click(await screen.findByRole('option', { name: 'n8' }));

    await waitFor(() => {
      expect(screen.getByTestId('default-artist')).toHaveTextContent('n8');
    });
    expect(server.writes).toEqual([{ ifMatch: '"1"', body: { defaultArtistId: N8.id } }]);
    expect(screen.getByText('New Songs are credited to n8.')).toBeVisible();
    expect(
      within(screen.getByTestId('default-artist')).getByRole('link', { name: 'n8' }),
    ).toHaveAttribute('href', `/artists/${N8.id}`);
  });

  it('clears the default Artist', async () => {
    const server = catalogServer({
      revision: 4,
      defaultArtist: { id: N8.id, name: N8.name },
    });
    const user = userEvent.setup();
    await openCatalog();

    await user.click(screen.getByRole('button', { name: 'Clear the default' }));

    await waitFor(() => {
      expect(screen.getByTestId('default-artist')).toHaveTextContent('No default Artist.');
    });
    expect(server.writes).toEqual([{ ifMatch: '"4"', body: { defaultArtistId: null } }]);
    expect(screen.getByText('New Songs are no longer credited to a default Artist.')).toBeVisible();
  });

  it('reloads the default when it was changed somewhere else', async () => {
    const server = catalogServer();
    const user = userEvent.setup();
    await openCatalog();
    server.changeElsewhere({ revision: 2, defaultArtist: { id: GUEST.id, name: GUEST.name } });

    await user.type(screen.getByRole('textbox', { name: 'Choose a default Artist' }), 'n');
    await user.click(await screen.findByRole('option', { name: 'n8' }));

    expect(
      await screen.findByText(
        'The default Artist was changed somewhere else, so it has been reloaded. Choose again if it is not what you want.',
      ),
    ).toBeVisible();
    expect(screen.getByTestId('default-artist')).toHaveTextContent('Guest Singer');
    expect(server.settings.revision).toBe(2);
  });
});
