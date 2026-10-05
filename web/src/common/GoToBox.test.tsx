import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { jsonResponse, renderApp, requestPath } from '../test/helpers';
import { baseSong } from '../test/songServer';
import { testVersion, versionServer } from '../test/versionServer';
import { GO_TO_FAILED, GO_TO_NOT_FOUND } from './GoToBox';

const VERSIONS = [testVersion('1', { current: true }), testVersion('1.1', { archived: true })];

function box() {
  return screen.getByRole('textbox', { name: 'Go to' });
}

/** Opens a page that loads nothing, so the only requests are the Go to box's. */
async function openElsewhere() {
  const { router } = renderApp('/nowhere');
  await screen.findByRole('heading', { level: 2, name: 'Page not found' });
  return router;
}

/** The selected Version's heading, once its pane has loaded (the loading pane is replaced). */
async function versionHeading(number: string) {
  await waitFor(() => {
    expect(screen.queryByText('Loading the lyrics and styles…')).toBeNull();
    expect(screen.getByRole('heading', { level: 3, name: `Version ${number}` })).toBeVisible();
  });
}

function resolveRequests(mock: ReturnType<typeof versionServer>['mock']) {
  return mock.mock.calls.filter(([input]) => requestPath(input).includes('/api/v1/resolve/'));
}

describe('the Go to box', () => {
  it('opens a Song from its shortcode on Enter and clears itself', async () => {
    const { mock } = versionServer(VERSIONS);
    const user = userEvent.setup();
    const router = await openElsewhere();

    await user.type(box(), 'n8-7');
    expect(resolveRequests(mock)).toHaveLength(0);
    await user.keyboard('{Enter}');

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' }),
    ).toBeVisible();
    expect(router.state.location.pathname).toBe('/songs/n8-7');
    expect(box()).toHaveValue('');
  });

  it('opens a Version from its shortcode in any letter case, ignoring surrounding whitespace', async () => {
    versionServer([testVersion('1'), testVersion('1.1', { current: true })]);
    const user = userEvent.setup();
    const router = await openElsewhere();

    await user.type(box(), '  N8-7-V1  {Enter}');

    await versionHeading('1');
    expect(router.state.location.pathname).toBe('/songs/n8-7/v/1');
  });

  it('acts on Enter, not on paste', async () => {
    const { mock } = versionServer(VERSIONS);
    const user = userEvent.setup();
    const router = await openElsewhere();

    await user.click(box());
    await user.paste('n8-7');
    expect(box()).toHaveValue('n8-7');
    expect(resolveRequests(mock)).toHaveLength(0);
    expect(router.state.location.pathname).toBe('/nowhere');

    await user.keyboard('{Enter}');
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/songs/n8-7');
    });
  });

  it('keeps an unknown reference in the box with a not-found message beside it', async () => {
    versionServer(VERSIONS);
    const user = userEvent.setup();
    const router = await openElsewhere();

    await user.type(box(), 'n8-9999{Enter}');

    expect(await screen.findByRole('alert')).toHaveTextContent(GO_TO_NOT_FOUND);
    expect(box()).toHaveValue('n8-9999');
    expect(box()).toHaveAttribute('aria-invalid', 'true');
    expect(box()).toHaveAccessibleDescription(GO_TO_NOT_FOUND);
    expect(router.state.location.pathname).toBe('/nowhere');

    // Leading zeros name nothing either; changing the text clears the message.
    await user.clear(box());
    expect(screen.queryByRole('alert')).toBeNull();
    await user.type(box(), 'n8-07{Enter}');
    expect(await screen.findByRole('alert')).toHaveTextContent(GO_TO_NOT_FOUND);
  });

  it('opens an archived Version, turning Show archived on', async () => {
    versionServer(VERSIONS);
    const user = userEvent.setup();
    const router = await openElsewhere();

    await user.type(box(), 'n8-7-v1.1{Enter}');

    await versionHeading('1.1');
    expect(router.state.location.pathname).toBe('/songs/n8-7/v/1.1');
    expect(screen.getByRole('switch', { name: 'Show archived' })).toBeChecked();
  });

  it('accepts a stable ID and n8Tracks links of this instance, but not of another host', async () => {
    const { mock } = versionServer(VERSIONS);
    const user = userEvent.setup();
    const router = await openElsewhere();

    await user.type(box(), `${baseSong.id.toUpperCase()}{Enter}`);
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/songs/n8-7');
    });

    await user.type(box(), `${new URL('go/n8-7-v1.1', document.baseURI).href}{Enter}`);
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/songs/n8-7/v/1.1');
    });

    // A Version page named by the Song's ID resolves the Song, then the Version.
    await user.type(box(), `${new URL(`songs/${baseSong.id}/v/1`, document.baseURI).href}{Enter}`);
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/songs/n8-7/v/1');
    });

    const before = resolveRequests(mock).length;
    await user.type(box(), 'https://elsewhere.example/go/n8-7{Enter}');
    expect(await screen.findByRole('alert')).toHaveTextContent(GO_TO_NOT_FOUND);
    await user.clear(box());
    await user.type(box(), `${new URL('settings/account', document.baseURI).href}{Enter}`);
    expect(await screen.findByRole('alert')).toHaveTextContent(GO_TO_NOT_FOUND);
    expect(resolveRequests(mock)).toHaveLength(before);
    expect(router.state.location.pathname).toBe('/songs/n8-7/v/1');
  });

  it('says so when n8Tracks does not answer', async () => {
    const { mock } = versionServer(VERSIONS);
    const answer = mock.getMockImplementation();
    mock.mockImplementation((input, init) =>
      requestPath(input).includes('/api/v1/resolve/')
        ? Promise.resolve(jsonResponse(500, { code: 'internal_error' }))
        : (answer?.(input, init) ?? Promise.reject(new Error('No answer.'))),
    );
    const user = userEvent.setup();
    await openElsewhere();

    await user.type(box(), 'n8-7{Enter}');

    expect(await screen.findByRole('alert')).toHaveTextContent(GO_TO_FAILED);
    expect(box()).toHaveValue('n8-7');
  });
});

describe('a /go/ link', () => {
  it('opens what it names in place of itself in the history', async () => {
    versionServer(VERSIONS);
    const { router } = renderApp('/go/N8-7-V1');

    await versionHeading('1');
    expect(router.state.location.pathname).toBe('/songs/n8-7/v/1');
    expect(router.state.historyAction).toBe('REPLACE');
  });

  it('works with a stable ID', async () => {
    versionServer(VERSIONS);
    const { router } = renderApp(`/go/${baseSong.id}`);

    expect(
      await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' }),
    ).toBeVisible();
    expect(router.state.location.pathname).toBe('/songs/n8-7');
  });

  it('shows a not-found page for a reference that names nothing', async () => {
    versionServer(VERSIONS);
    const { router } = renderApp('/go/n8-404');

    expect(await screen.findByRole('heading', { level: 2, name: 'Not found' })).toBeVisible();
    expect(screen.getByText(/Nothing has the shortcode or ID n8-404\./)).toBeVisible();
    expect(router.state.location.pathname).toBe('/go/n8-404');
  });
});
