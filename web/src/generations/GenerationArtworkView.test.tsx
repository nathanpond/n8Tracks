import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderApp } from '../test/helpers';
import { testArtwork, testAssetId } from '../test/songServer';
import { testGeneration, testVersion, versionServer } from '../test/versionServer';

const IMAGE = testArtwork(testAssetId(3), { width: 1024, height: 1024 });

function generationRow(shortcode: string): HTMLElement {
  const row = document.querySelector(`tr[data-generation="${shortcode}"]`);
  if (!(row instanceof HTMLElement)) {
    throw new Error(`Generation ${shortcode} is not listed.`);
  }
  return row;
}

function songWithImages() {
  const { server } = versionServer([testVersion('1', { current: true, isFrozen: true })]);
  server.generations = [testGeneration('1', 1, { artwork: IMAGE }), testGeneration('1', 2)];
  return server;
}

describe('a Generation’s image (#121)', () => {
  it('shows on its row, and a Generation without one shows none', async () => {
    const user = userEvent.setup();
    songWithImages();
    renderApp('/songs/n8-7');
    await user.click(await screen.findByRole('button', { name: 'Generations of Version 1' }));
    await waitFor(() => {
      expect(document.querySelector('tr[data-generation="n8-7-v1-g1"]')).not.toBeNull();
    });

    expect(
      within(generationRow('n8-7-v1-g1')).getByRole('img', { name: 'Artwork for n8-7-v1-g1' }),
    ).toHaveAttribute('src', IMAGE.squareUrls['96']);
    expect(within(generationRow('n8-7-v1-g2')).queryByRole('img')).toBeNull();
  });

  it('shows whole in its panel, or says it has none', async () => {
    songWithImages();
    renderApp('/songs/n8-7/generations/n8-7-v1-g1');
    const panel = await screen.findByRole('dialog', { name: 'Generation n8-7-v1-g1' });
    expect(
      within(within(panel).getByTestId('generation-panel-artwork')).getByRole('img', {
        name: 'Artwork for n8-7-v1-g1',
      }),
    ).toHaveAttribute('src', IMAGE.squareUrls['320']);
  });

  it('says a Generation has no image yet in its panel', async () => {
    songWithImages();
    renderApp('/songs/n8-7/generations/n8-7-v1-g2');
    const panel = await screen.findByRole('dialog', { name: 'Generation n8-7-v1-g2' });
    expect(within(panel).getByTestId('generation-panel-artwork')).toHaveTextContent(
      'No image from Suno yet.',
    );
  });
});
