import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { NO_RELEASE } from '../../api/songs';
import { jsonResponse, renderApp } from '../../test/helpers';
import { baseSong, SONG_B, songServer } from '../../test/songServer';

async function openRelease(user: ReturnType<typeof userEvent.setup>) {
  renderApp('/songs/n8-7');
  await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' });
  await user.click(screen.getByRole('button', { name: 'Details' }));
  return screen.findByRole('group', { name: 'Release' });
}

const textbox = (name: string) => screen.getByRole('textbox', { name });

describe('the Release section of the Details panel', () => {
  it('saves a release date, the explicit flag, an ISRC, and a language, each on the Song’s revision', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    await openRelease(user);

    await user.type(textbox('Release date'), ' 2026-03 ');
    await user.tab();
    expect(await screen.findByTestId('releaseDate-shown')).toHaveTextContent(/2026/);
    expect(textbox('Release date')).toHaveValue('2026-03');

    await user.click(screen.getByRole('radio', { name: 'Explicit' }));
    await waitFor(() => {
      expect(screen.getByRole('radio', { name: 'Explicit' })).toBeChecked();
    });

    await user.type(textbox('ISRC'), 'us-s1z-99-00001{Enter}');
    await waitFor(() => {
      expect(textbox('ISRC')).toHaveValue('USS1Z9900001');
    });

    await waitFor(() => {
      expect(screen.getByRole('combobox', { name: 'Language' })).toBeEnabled();
    });
    await user.selectOptions(screen.getByRole('combobox', { name: 'Language' }), 'French');
    await waitFor(() => {
      expect(screen.getByRole('combobox', { name: 'Language' })).toHaveValue('fr');
    });

    expect(server.edits).toEqual([
      { ifMatch: '"1"', body: { release: { releaseDate: '2026-03' } } },
      { ifMatch: '"2"', body: { release: { explicit: 'explicit' } } },
      { ifMatch: '"3"', body: { release: { isrc: 'USS1Z9900001' } } },
      { ifMatch: '"4"', body: { release: { language: 'fr' } } },
    ]);
    expect(server.song.release).toEqual({
      ...NO_RELEASE,
      releaseDate: '2026-03',
      explicit: 'explicit',
      isrc: 'USS1Z9900001',
      language: 'fr',
    });
  });

  it('refuses an ISRC of eleven characters with a field error and sends nothing', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    await openRelease(user);

    await user.type(textbox('ISRC'), 'US-S1Z-99-0000{Enter}');

    expect(
      await screen.findByText(
        'An ISRC has 12 characters once spaces and hyphens are left out; this has 11.',
      ),
    ).toBeVisible();
    expect(textbox('ISRC')).toHaveAttribute('aria-invalid', 'true');
    expect(server.edits).toEqual([]);
  });

  it('shows the API’s refusal of a member at its field', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    await openRelease(user);
    server.next = () =>
      jsonResponse(422, {
        code: 'validation_failed',
        errors: { 'release.copyright': ['Use at most 500 characters.'] },
      });

    await user.type(textbox('Copyright'), '℗ 2026 n8');
    await user.tab();

    expect(await screen.findByText('Use at most 500 characters.')).toBeVisible();
    expect(textbox('Copyright')).toHaveAttribute('aria-invalid', 'true');
  });

  it('shows a choice as made while it saves, and the Song’s own value again when it fails', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    await openRelease(user);
    let answer: (response: Response) => void = () => undefined;
    server.next = () =>
      new Promise<Response>((resolve) => {
        answer = resolve;
      });

    await user.click(screen.getByRole('radio', { name: 'Clean' }));
    expect(screen.getByRole('radio', { name: 'Clean' })).toBeChecked();
    expect(screen.getByRole('radio', { name: 'Explicit' })).toBeDisabled();

    answer(jsonResponse(500, { code: 'unexpected' }));
    await waitFor(() => {
      expect(screen.getByRole('radio', { name: 'Not set' })).toBeChecked();
    });
    expect(screen.getByRole('radio', { name: 'Explicit' })).toBeEnabled();
    expect(server.song.release.explicit).toBeNull();
  });

  it('warns while another Song has the ISRC, naming and linking it, and keeps the ISRC', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    server.others = [{ ...SONG_B, release: { ...NO_RELEASE, isrc: 'USS1Z9900001' } }];
    await openRelease(user);

    await user.type(textbox('ISRC'), 'USS1Z9900001');
    await user.tab();

    const warning = await screen.findByTestId('isrc-warning');
    expect(within(warning).getByText('Another Song has this ISRC.')).toBeVisible();
    expect(within(warning).getByRole('link', { name: 'Song B' })).toHaveAttribute(
      'href',
      '/songs/n8-8',
    );
    expect(server.song.release.isrc).toBe('USS1Z9900001');

    // Gone once the ISRC is changed.
    await user.clear(textbox('ISRC'));
    await user.type(textbox('ISRC'), 'GB-AAA-26-12345');
    await user.tab();
    await waitFor(() => {
      expect(screen.queryByTestId('isrc-warning')).not.toBeInTheDocument();
    });
  });

  it('clears members: an emptied field and Not set each save null', async () => {
    const user = userEvent.setup();
    const { server } = songServer({
      ...baseSong,
      release: { ...NO_RELEASE, originalReleaseDate: '1999', explicit: 'clean', language: 'en' },
    });
    await openRelease(user);
    expect(screen.getByRole('radio', { name: 'Clean' })).toBeChecked();
    expect(await screen.findByTestId('originalReleaseDate-shown')).toHaveTextContent('1999');

    await user.clear(textbox('Original release date'));
    await user.tab();
    await waitFor(() => {
      expect(screen.queryByTestId('originalReleaseDate-shown')).not.toBeInTheDocument();
    });
    await user.click(screen.getByRole('radio', { name: 'Not set' }));
    await waitFor(() => {
      expect(screen.getByRole('radio', { name: 'Not set' })).toBeChecked();
    });
    await waitFor(() => {
      expect(screen.getByRole('combobox', { name: 'Language' })).toBeEnabled();
    });
    await user.selectOptions(screen.getByRole('combobox', { name: 'Language' }), 'Not set');

    await waitFor(() => {
      expect(server.song.release).toEqual(NO_RELEASE);
    });
    expect(server.edits.map((edit) => edit.body)).toEqual([
      { release: { originalReleaseDate: null } },
      { release: { explicit: null } },
      { release: { language: null } },
    ]);
  });

  it('saves links as a whole list, each checked first', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const section = await openRelease(user);

    await user.click(within(section).getByRole('button', { name: 'Add link' }));
    await user.type(textbox('Link 1 label'), 'Spotify');
    await user.type(textbox('Link 1 URL'), 'ftp://example.com');
    await user.click(within(section).getByRole('button', { name: 'Save links' }));
    expect(
      await screen.findByText('Enter a web address starting with http:// or https://.'),
    ).toBeVisible();
    expect(server.edits).toEqual([]);

    await user.clear(textbox('Link 1 URL'));
    await user.type(textbox('Link 1 URL'), 'https://open.spotify.com/track/1');
    await user.click(within(section).getByRole('button', { name: 'Save links' }));

    expect(await within(section).findByRole('link', { name: 'Spotify' })).toHaveAttribute(
      'href',
      'https://open.spotify.com/track/1',
    );
    expect(server.edits.map((edit) => edit.body)).toEqual([
      { release: { links: [{ label: 'Spotify', url: 'https://open.spotify.com/track/1' }] } },
    ]);
  });

  it('offers to try again when the language list cannot be loaded', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    server.languages = undefined;
    const section = await openRelease(user);

    expect(await screen.findByText('The language list could not be loaded.')).toBeVisible();
    expect(screen.getByRole('combobox', { name: 'Language' })).toBeDisabled();

    server.languages = [{ code: 'en', name: 'English' }];
    await user.click(within(section).getByRole('button', { name: 'Try again' }));
    await waitFor(() => {
      expect(screen.getByRole('combobox', { name: 'Language' })).toBeEnabled();
    });
    expect(screen.getByRole('option', { name: 'English' })).toBeInTheDocument();
  });
});
