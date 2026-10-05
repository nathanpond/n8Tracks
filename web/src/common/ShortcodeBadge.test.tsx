import { MantineProvider } from '@mantine/core';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderApp } from '../test/helpers';
import { testVersion, versionServer } from '../test/versionServer';
import { COPIED_NOTICE_MS, ShortcodeBadge } from './ShortcodeBadge';

function renderBadge(shortcode: string) {
  return render(
    <MantineProvider>
      <ShortcodeBadge shortcode={shortcode} />
    </MantineProvider>,
  );
}

/** Replaces the clipboard userEvent installs; `undefined` plays a browser without one. */
function setClipboard(clipboard: Partial<Clipboard> | undefined) {
  Object.defineProperty(window.navigator, 'clipboard', { value: clipboard, configurable: true });
}

afterEach(() => {
  Reflect.deleteProperty(window.navigator, 'clipboard');
});

describe('the shortcode copy control', () => {
  it('shows the shortcode in lower case and copies it in one click, confirming for two seconds', async () => {
    const user = userEvent.setup();
    renderBadge('N8-12-V1.1');

    expect(screen.getByTestId('shortcode')).toHaveTextContent(/^n8-12-v1\.1$/);
    await user.click(screen.getByRole('button', { name: 'Copy shortcode n8-12-v1.1' }));

    expect(await navigator.clipboard.readText()).toBe('n8-12-v1.1');
    const status = screen.getByRole('status');
    expect(status).toHaveAttribute('aria-live', 'polite');
    expect(status).toHaveTextContent('Copied');

    await waitFor(
      () => {
        expect(status).toHaveTextContent('');
      },
      { timeout: COPIED_NOTICE_MS + 1000 },
    );
  });

  it('selects the shortcode for manual copying where there is no clipboard', async () => {
    const user = userEvent.setup();
    setClipboard(undefined);
    renderBadge('n8-12');

    await user.click(screen.getByRole('button', { name: 'Copy shortcode n8-12' }));

    const field = await screen.findByRole('textbox', { name: 'Shortcode n8-12' });
    expect(field).toHaveValue('n8-12');
    expect(field).toHaveAttribute('readonly');
    await waitFor(() => {
      expect(field).toHaveFocus();
    });
    expect((field as HTMLInputElement).selectionStart).toBe(0);
    expect((field as HTMLInputElement).selectionEnd).toBe('n8-12'.length);
    expect(screen.getByRole('status')).toHaveTextContent(
      'Copying is not available here. The shortcode is selected: press Ctrl+C or Cmd+C.',
    );
    expect(screen.queryByText('Copied')).toBeNull();
  });

  it('falls back to manual copying when the browser refuses the clipboard', async () => {
    const user = userEvent.setup();
    const writeText = vi.fn(() => Promise.reject(new Error('Not allowed.')));
    setClipboard({ writeText });
    renderBadge('n8-3-v2');

    await user.click(screen.getByRole('button', { name: 'Copy shortcode n8-3-v2' }));

    expect(writeText).toHaveBeenCalledWith('n8-3-v2');
    expect(await screen.findByRole('textbox', { name: 'Shortcode n8-3-v2' })).toHaveValue(
      'n8-3-v2',
    );

    // Leaving the field puts the plain shortcode back.
    await user.tab();
    await waitFor(() => {
      expect(screen.queryByRole('textbox', { name: 'Shortcode n8-3-v2' })).toBeNull();
    });
    expect(screen.getByTestId('shortcode')).toHaveTextContent('n8-3-v2');
  });

  it('is on the Song page and the Version panel, each with its own shortcode', async () => {
    versionServer([testVersion('1'), testVersion('1.1', { current: true })]);
    const user = userEvent.setup();
    renderApp('/songs/n8-7');

    expect(await screen.findByTestId('version-shortcode')).toHaveTextContent('n8-7-v1.1');
    expect(screen.getByTestId('shortcode')).toHaveTextContent(/^n8-7$/);

    await user.click(screen.getByRole('button', { name: 'Copy shortcode n8-7-v1.1' }));
    expect(await navigator.clipboard.readText()).toBe('n8-7-v1.1');
    expect(await screen.findByText('Copied')).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Copy shortcode n8-7' }));
    expect(await navigator.clipboard.readText()).toBe('n8-7');
  });
});
