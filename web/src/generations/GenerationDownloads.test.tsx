import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { mediaFolderText, type GenerationDownload } from '../api/downloadRecords';
import { jsonResponse, renderApp, requestPath } from '../test/helpers';
import { testGeneration, testVersion, versionServer } from '../test/versionServer';

/**
 * The Generation panel's Downloads (#222): the files the extension downloaded for the clip, newest
 * first, each with its format, file name, and time, and whether a scanned file of its name is in
 * the media folder and attached to this Generation.
 */

const ONE = testVersion('1', { current: true, isFrozen: true });
const G1 = 'n8-7-v1-g1';

function download(
  fileName: string,
  match: GenerationDownload['mediaFolder']['match'],
  change: Partial<GenerationDownload> = {},
  status: 'available' | 'missing' | 'unavailable' = 'available',
): GenerationDownload {
  return {
    id: `record-${fileName}`,
    sunoId: '0c90d621-e30c-4c76-814a-e1fdeb500582',
    format: 'wav',
    fileName,
    completedAt: '2026-10-05T14:30:00Z',
    receivedAt: '2026-10-05T14:30:02Z',
    sizeBytes: 1024,
    spentUnlock: false,
    mediaFolder: {
      match,
      audioFile: match === 'not-found' ? null : { id: `file-${fileName}`, status },
    },
    ...change,
  };
}

/** The media folder line of a listed record. */
function media(item: HTMLElement | undefined): HTMLElement {
  if (item === undefined) {
    throw new Error('The record is not listed.');
  }
  return within(item).getByTestId('generation-download-media');
}

async function openDownloads() {
  renderApp(`/songs/n8-7/generations/${G1}`);
  const panel = await screen.findByRole('dialog', { name: `Generation ${G1}` });
  await waitFor(() => {
    expect(panel).toBeVisible();
  });
  return within(panel).getByRole('region', { name: 'Downloads' });
}

describe('the Generation panel’s downloads', () => {
  it('lists each record with its format, name, time, and whether it is in the media folder', async () => {
    const { server } = versionServer([ONE]);
    const generation = testGeneration('1', 1);
    server.generations = [generation];
    server.downloads.set(generation.id, [
      download('Song (suno-x).wav', 'attached', { spentUnlock: true }),
      download('Song (suno-x) (1).mp3', 'elsewhere', { format: 'mp3' }, 'missing'),
      download('Song (suno-x) stream.m4a', 'not-found', { format: 'm4a-stream' }),
    ]);

    const region = await openDownloads();

    const items = await within(region).findAllByTestId('generation-download');
    expect(items.map((item) => item.querySelector('p')?.textContent)).toEqual([
      'Song (suno-x).wav',
      'Song (suno-x) (1).mp3',
      'Song (suno-x) stream.m4a',
    ]);
    const [attached, elsewhere, missing] = items;
    expect(attached).toHaveTextContent('WAV');
    expect(attached).toHaveTextContent('Used a Suno download unlock');
    expect(media(attached)).toHaveTextContent('In media folder');
    expect(media(elsewhere)).toHaveTextContent(
      'Found, not attached to this Generation (Missing: not found by the last scan)',
    );
    expect(elsewhere).not.toHaveTextContent('Used a Suno download unlock');
    expect(missing).toHaveTextContent('M4A (streaming quality)');
    expect(media(missing)).toHaveTextContent('Not found in media folder');
    expect(attached?.querySelector('time')?.getAttribute('datetime')).toBe('2026-10-05T14:30:00Z');
  });

  it('says when nothing was downloaded', async () => {
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1)];

    const region = await openDownloads();

    expect(await within(region).findByText(/No downloads recorded/)).toBeInTheDocument();
  });

  it('offers to try again when the records cannot be read', async () => {
    const { server, mock: fetchMock } = versionServer([ONE]);
    const generation = testGeneration('1', 1);
    server.generations = [generation];
    let fail = true;
    const answer = fetchMock.getMockImplementation();
    fetchMock.mockImplementation((input, init) => {
      if (fail && requestPath(input).endsWith(`/generations/${generation.id}/downloads`)) {
        return Promise.resolve(jsonResponse(500, { code: 'internal' }));
      }
      return answer === undefined ? Promise.reject(new Error('no fake')) : answer(input, init);
    });

    const region = await openDownloads();
    expect(await within(region).findByRole('alert')).toHaveTextContent(
      'The downloads could not be loaded.',
    );
    fail = false;
    await userEvent.click(within(region).getByRole('button', { name: 'Try again' }));

    expect(await within(region).findByText(/No downloads recorded/)).toBeInTheDocument();
  });

  it('words the media folder states, with a file’s own state', () => {
    expect(mediaFolderText(download('a.wav', 'attached'))).toBe('In media folder');
    expect(mediaFolderText(download('a.wav', 'attached', {}, 'unavailable'))).toBe(
      'In media folder (the media folder is unavailable)',
    );
    expect(mediaFolderText(download('a.wav', 'elsewhere'))).toBe(
      'Found, not attached to this Generation',
    );
    expect(mediaFolderText(download('a.wav', 'not-found'))).toBe('Not found in media folder');
  });
});
