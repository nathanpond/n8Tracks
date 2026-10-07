import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { GENERATING_REFRESH_MS } from '../api/generations';
import { advanceTimers, fakeTimeouts, jsonResponse, renderApp } from '../test/helpers';
import { testComment, testGeneration, testVersion, versionServer } from '../test/versionServer';

const ONE = testVersion('1', { current: true, isFrozen: true });

/** The Versions and Generations section. */
function section() {
  return screen.getByRole('region', { name: 'Versions and Generations' });
}

/** The table of Versions, once the Generations are in. */
function table() {
  return within(section()).getByRole('table', { name: 'Versions and Generations' });
}

/** The row of the Version numbered `number`, or null when it is not listed. */
function versionRow(number: string): HTMLElement | null {
  const row = table().querySelector(`tr[data-version-row="${number}"]`);
  return row instanceof HTMLElement ? row : null;
}

function requireRow(number: string): HTMLElement {
  const row = versionRow(number);
  if (row === null) {
    throw new Error(`Version ${number} is not listed.`);
  }
  return row;
}

/** The numbers of the Versions listed, in order. */
function listed(): string[] {
  return [...table().querySelectorAll('tr[data-version-row]')].map(
    (row) => row.getAttribute('data-version-row') ?? '?',
  );
}

/** The shortcodes of the Generations listed under Version `number`, in order. */
function generationsUnder(number: string): string[] {
  const nested = table().querySelector(`table[data-generations-of="${number}"]`);
  return [...(nested?.querySelectorAll('tr[data-generation]') ?? [])].map(
    (row) => row.getAttribute('data-generation') ?? '?',
  );
}

function generationRow(shortcode: string): HTMLElement {
  const row = table().querySelector(`tr[data-generation="${shortcode}"]`);
  if (!(row instanceof HTMLElement)) {
    throw new Error(`Generation ${shortcode} is not listed.`);
  }
  return row;
}

/** The Generation panel named `name`, once its opening transition has finished. */
async function openedPanel(name: string): Promise<HTMLElement> {
  const panel = await screen.findByRole('dialog', { name });
  await waitFor(() => {
    expect(panel).toBeVisible();
  });
  return panel;
}

/** The Version tree's node for the Version numbered `number`. */
function treeNode(number: string): HTMLElement {
  const node = screen
    .getByRole('tree', { name: 'Versions' })
    .querySelector(`[role="treeitem"][data-version-number="${number}"]`);
  if (!(node instanceof HTMLElement)) {
    throw new Error(`Version ${number} is not in the tree.`);
  }
  return node;
}

function expander(number: string) {
  return within(section()).getByRole('button', { name: `Generations of Version ${number}` });
}

async function openSong(path = '/songs/n8-7') {
  const rendered = renderApp(path);
  await screen.findByRole('table', { name: 'Versions and Generations' });
  await waitFor(() => {
    expect(screen.queryByText('Loading the lyrics and styles…')).toBeNull();
  });
  return rendered;
}

describe('the Versions table', () => {
  it('lists each listed Version once, in tree order, with its details, counts, and highest rating', async () => {
    const { server } = versionServer([
      testVersion('2', { kind: 'speech', updatedAt: '2026-10-02T15:45:00Z' }),
      ONE,
      testVersion('1.10'),
      testVersion('1.2', { name: 'Slower', isFrozen: true }),
    ]);
    server.generations = [
      testGeneration('1', 1, { rating: 3 }),
      testGeneration('1', 2, { rating: 4, state: 'archived' }),
      testGeneration('1', 3),
      testGeneration('1.2', 1),
    ];

    await openSong();

    expect(listed()).toEqual(['1', '1.2', '1.10', '2']);
    const one = requireRow('1');
    expect(within(one).getByRole('link', { name: 'Version 1' })).toHaveAttribute(
      'aria-current',
      'true',
    );
    expect(within(one).getByText('Current')).toBeVisible();
    expect(within(one).getByText('Frozen')).toBeVisible();
    expect(within(one).getByText('Song')).toBeVisible();
    // Counts and the highest rating cover every live Generation, archived ones included.
    expect(within(one).getByTestId('generation-count')).toHaveTextContent('3');
    expect(within(one).getByTestId('highest-rating')).toHaveTextContent('4 of 5 stars');

    const slower = requireRow('1.2');
    expect(within(slower).getByText('Slower')).toBeVisible();
    expect(within(slower).queryByText('Current')).toBeNull();
    expect(within(slower).getByTestId('generation-count')).toHaveTextContent('1');
    expect(within(slower).getByTestId('highest-rating')).toHaveTextContent('Not rated');

    const two = requireRow('2');
    expect(within(two).getByText('Speech')).toBeVisible();
    expect(within(two).queryByText('Frozen')).toBeNull();
    expect(within(two).getByTestId('generation-count')).toHaveTextContent('0');
    expect(within(two).getByRole('time')).toHaveAttribute('dateTime', '2026-10-02T15:45:00Z');
    // One request for the whole Song's Generations.
    expect(server.generationReads).toBe(1);
  });

  it('lists archived Versions only while Show archived Versions is on, kept in the URL', async () => {
    const user = userEvent.setup();
    versionServer([ONE, testVersion('2', { archived: true }), testVersion('3')]);

    const { router } = await openSong();

    // Complement: an Archived Version is absent with the toggle off.
    expect(listed()).toEqual(['1', '3']);
    const toggle = within(section()).getByRole('switch', { name: 'Show archived Versions' });
    expect(toggle).not.toBeChecked();

    await user.click(toggle);

    expect(listed()).toEqual(['1', '2', '3']);
    expect(within(requireRow('2')).getByText('Archived')).toBeVisible();
    expect(router.state.location.search).toBe('?archived=1');

    await user.click(toggle);
    expect(listed()).toEqual(['1', '3']);
    expect(router.state.location.search).toBe('');
  });

  it('leaves out an archived current Version too while Show archived Versions is off', async () => {
    versionServer([testVersion('1', { current: true, archived: true }), testVersion('2')]);

    await openSong();

    expect(listed()).toEqual(['2']);
    // The tree and the editor still show it.
    expect(treeNode('1')).toHaveAttribute('aria-selected', 'true');
  });

  it('opens with archived Versions listed when the URL says so, and the tree keeps its own choice', async () => {
    versionServer([ONE, testVersion('2', { archived: true })]);

    await openSong('/songs/n8-7?archived=1');

    expect(listed()).toEqual(['1', '2']);
    expect(within(section()).getByRole('switch', { name: 'Show archived Versions' })).toBeChecked();
    expect(screen.getByRole('switch', { name: 'Show archived' })).not.toBeChecked();
  });

  it('never lists a deleted Version placeholder as a row', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('1.1.1')]);
    // 1.1 was deleted: the tree holds its place above 1.1.1.
    server.usedNumbers.add('1.1');

    await openSong('/songs/n8-7?archived=1');

    expect(screen.getByRole('treeitem', { name: /Deleted Version 1\.1/ })).toBeVisible();
    expect(listed()).toEqual(['1', '1.1.1']);
    await user.click(within(section()).getByRole('switch', { name: 'Show archived Versions' }));
    expect(listed()).toEqual(['1', '1.1.1']);
    expect(within(section()).queryByText(/Deleted/)).toBeNull();
  });

  it("expands a Version with its chevron to show its Generations' details, in ordinal order", async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('2')]);
    server.generations = [
      testGeneration('1', 2, {
        title: 'Second take',
        durationSeconds: 187.4,
        modelLabel: null,
        modelName: 'v4.5+',
        rating: 5,
        comments: [testComment(1), testComment(2)],
      }),
      testGeneration('1', 1, { sunoCreatedAt: '2026-09-30T08:05:00Z' }),
      testGeneration('2', 1),
    ];

    await openSong();

    // Nothing is expanded at first, and choosing a row's Version does not expand it.
    expect(expander('1')).toHaveAttribute('aria-expanded', 'false');
    expect(generationsUnder('1')).toEqual([]);
    await user.click(within(requireRow('1')).getByRole('link', { name: 'Version 1' }));
    expect(expander('1')).toHaveAttribute('aria-expanded', 'false');

    await user.click(expander('1'));

    expect(expander('1')).toHaveAttribute('aria-expanded', 'true');
    expect(generationsUnder('1')).toEqual(['n8-7-v1-g1', 'n8-7-v1-g2']);
    expect(generationsUnder('2')).toEqual([]);

    const first = generationRow('n8-7-v1-g1');
    expect(within(first).getByTestId('generation-shortcode')).toHaveTextContent('n8-7-v1-g1');
    expect(within(first).getByRole('link', { name: 'Take 1, n8-7-v1-g1' })).toBeVisible();
    expect(within(first).getByTestId('generation-duration')).toHaveTextContent('2:05');
    expect(within(first).getByText('v5')).toBeVisible();
    const unrated = within(first).getByRole('radiogroup', { name: 'Rating of n8-7-v1-g1' });
    expect(within(unrated).queryByRole('radio', { checked: true })).toBeNull();
    expect(within(first).getByTestId('generation-comment-count')).toHaveTextContent('0');
    expect(within(first).getByText('Active')).toBeVisible();
    expect(within(first).getByRole('time')).toHaveAttribute('dateTime', '2026-09-30T08:05:00Z');
    expect(
      within(first).getByRole('link', { name: 'Open in Suno: n8-7-v1-g1 (opens a new tab)' }),
    ).toHaveAttribute('href', 'https://suno.com/song/suno-n8-7-v1-g1');

    const second = generationRow('n8-7-v1-g2');
    expect(within(second).getByTestId('generation-duration')).toHaveTextContent('3:07');
    expect(within(second).getByText('v4.5+')).toBeVisible();
    expect(within(second).getByRole('radio', { name: '5 stars' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
    expect(within(second).getByTestId('generation-comment-count')).toHaveTextContent('2');

    await user.click(expander('1'));
    expect(generationsUnder('1')).toEqual([]);
  });

  it('can be collapsed and shown again', async () => {
    const user = userEvent.setup();
    versionServer([ONE]);

    await openSong();
    const control = within(section()).getByRole('button', { name: 'Hide the table' });
    expect(control).toHaveAttribute('aria-expanded', 'true');

    await user.click(control);

    expect(within(section()).queryByRole('table')).toBeNull();
    await user.click(within(section()).getByRole('button', { name: 'Show the table' }));
    expect(listed()).toEqual(['1']);
  });

  it('says "No Generations yet" for an expanded Version without any', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('2')]);
    server.generations = [testGeneration('1', 1)];

    await openSong();
    await user.click(expander('2'));

    expect(within(section()).getByTestId('no-generations')).toHaveTextContent('No Generations yet');
  });

  it('hides archived Generations until their toggle is on, but always lists the Selected one', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [
      testGeneration('1', 1),
      testGeneration('1', 2, { state: 'archived' }),
      testGeneration('1', 3, { state: 'archived', isSelected: true }),
    ];

    const { router } = await openSong();
    await user.click(expander('1'));

    // Complement: an archived Generation is absent with its toggle off; the Selected one stays.
    expect(generationsUnder('1')).toEqual(['n8-7-v1-g1', 'n8-7-v1-g3']);
    const selected = generationRow('n8-7-v1-g3');
    expect(within(selected).getByTestId('selected-generation')).toHaveTextContent('Selected');
    expect(within(selected).getByText('Archived')).toBeVisible();
    expect(within(generationRow('n8-7-v1-g1')).queryByTestId('selected-generation')).toBeNull();

    await user.click(within(section()).getByRole('switch', { name: 'Show archived Generations' }));

    expect(generationsUnder('1')).toEqual(['n8-7-v1-g1', 'n8-7-v1-g2', 'n8-7-v1-g3']);
    expect(router.state.location.search).toBe('?archivedGenerations=1');
  });

  it('says how many archived Generations are hidden when they are all a Version has', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1, { state: 'archived' })];

    await openSong();
    await user.click(expander('1'));

    expect(within(section()).getByTestId('generations-hidden')).toHaveTextContent(
      '1 archived Generation is hidden.',
    );
  });

  it('shows Remote Missing with what it means, and no Suno link without a Suno ID', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [
      testGeneration('1', 1, { remoteState: 'missing' }),
      testGeneration('1', 2, { sunoId: null, title: null, durationSeconds: null }),
    ];

    await openSong();
    await user.click(expander('1'));

    const missing = generationRow('n8-7-v1-g1');
    expect(within(missing).getByText('Remote Missing')).toBeVisible();
    expect(within(missing).getByText(/Suno no longer lists this clip/)).toBeInTheDocument();
    const manual = generationRow('n8-7-v1-g2');
    expect(within(manual).queryByRole('link', { name: /Open in Suno/ })).toBeNull();
    expect(within(manual).getByRole('link', { name: 'Untitled, n8-7-v1-g2' })).toBeVisible();
    expect(within(manual).getByTestId('generation-duration')).toHaveTextContent('Unknown');
  });

  it('shows In Suno Trash on a Generation whose clip a sync found in Suno’s Trash (#142)', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [
      testGeneration('1', 1, { state: 'archived', remoteState: 'trashed' }),
      testGeneration('1', 2),
    ];

    await openSong();
    await user.click(expander('1'));
    await user.click(within(section()).getByRole('switch', { name: 'Show archived Generations' }));

    expect(within(generationRow('n8-7-v1-g1')).getByTestId('in-suno-trash')).toHaveTextContent(
      'In Suno Trash',
    );
    expect(within(generationRow('n8-7-v1-g2')).queryByTestId('in-suno-trash')).toBeNull();
  });

  it('copies a Generation shortcode in one click', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1), testGeneration('1', 2)];

    await openSong();
    await user.click(expander('1'));
    await user.click(
      within(generationRow('n8-7-v1-g2')).getByRole('button', {
        name: 'Copy shortcode n8-7-v1-g2',
      }),
    );

    expect(await navigator.clipboard.readText()).toBe('n8-7-v1-g2');
  });

  it('opens a Version in the editor and the tree from its row without making it current', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('2')]);

    const { router } = await openSong('/songs/n8-7?archivedGenerations=1');
    await user.click(within(requireRow('2')).getByRole('link', { name: 'Version 2' }));

    expect(await screen.findByRole('heading', { level: 3, name: 'Version 2' })).toBeVisible();
    expect(router.state.location.pathname).toBe('/songs/n8-7/v/2');
    // The table's choices stay in the URL.
    expect(router.state.location.search).toBe('?archivedGenerations=1');
    expect(treeNode('2')).toHaveAttribute('aria-selected', 'true');
    expect(within(requireRow('2')).getByRole('link', { name: 'Version 2' })).toHaveAttribute(
      'aria-current',
      'true',
    );
    expect(within(requireRow('1')).getByText('Current')).toBeVisible();
    expect(within(requireRow('2')).queryByText('Current')).toBeNull();
    expect(server.writes).toEqual([]);

    // Selecting in the tree selects in the table too.
    await user.click(treeNode('1'));
    await waitFor(() => {
      expect(within(requireRow('1')).getByRole('link', { name: 'Version 1' })).toHaveAttribute(
        'aria-current',
        'true',
      );
    });
  });

  it('shows "Generating" for a clip still being made, and reads the list again every 10 seconds until it is done', async () => {
    fakeTimeouts();
    const user = userEvent.setup({ advanceTimers });
    const { server } = versionServer([ONE]);
    server.generations = [
      testGeneration('1', 1, { providerStatus: 'streaming', durationSeconds: null }),
      testGeneration('1', 2, { providerStatus: 'submitted', durationSeconds: null }),
    ];

    await openSong();
    await user.click(expander('1'));
    expect(
      within(generationRow('n8-7-v1-g1')).getByTestId('generation-duration'),
    ).toHaveTextContent('Generating');
    expect(
      within(generationRow('n8-7-v1-g2')).getByTestId('generation-duration'),
    ).toHaveTextContent('Generating');
    expect(server.generationReads).toBe(1);

    act(() => {
      advanceTimers(GENERATING_REFRESH_MS - 100);
    });
    expect(server.generationReads).toBe(1);
    server.generations = [
      testGeneration('1', 1, { providerStatus: 'complete', durationSeconds: 61 }),
      testGeneration('1', 2, { providerStatus: 'complete', durationSeconds: 62 }),
    ];
    act(() => {
      advanceTimers(100);
    });

    await waitFor(() => {
      expect(
        within(generationRow('n8-7-v1-g1')).getByTestId('generation-duration'),
      ).toHaveTextContent('1:01');
    });
    expect(server.generationReads).toBe(2);
    act(() => {
      advanceTimers(GENERATING_REFRESH_MS * 3);
    });
    expect(server.generationReads).toBe(2);
  });

  it('keeps the list it shows when a read again fails, and offers to try a failed first read again', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.nextGenerations = () => jsonResponse(500, { code: 'internal_error' });

    renderApp('/songs/n8-7');

    expect(await screen.findByText('The Generations could not be loaded')).toBeVisible();
    server.generations = [testGeneration('1', 1)];
    await user.click(within(section()).getByRole('button', { name: 'Try again' }));

    await screen.findByRole('table', { name: 'Versions and Generations' });
    expect(within(requireRow('1')).getByTestId('generation-count')).toHaveTextContent('1');
  });
});

describe('the Generation panel', () => {
  it('opens from a Generation row at its own address, with the same details, and closes onto its Version', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE, testVersion('2', { current: false })]);
    server.generations = [
      testGeneration('1', 1),
      testGeneration('1', 2, { title: 'Second take', rating: 4, comments: [testComment(1)] }),
    ];

    const { router } = await openSong('/songs/n8-7/v/2');
    await user.click(expander('1'));
    await user.click(
      within(generationRow('n8-7-v1-g2')).getByRole('link', { name: 'Second take, n8-7-v1-g2' }),
    );

    const panel = await openedPanel('Generation n8-7-v1-g2');
    expect(router.state.location.pathname).toBe('/songs/n8-7/generations/n8-7-v1-g2');
    expect(within(panel).getByTestId('generation-panel-shortcode')).toHaveTextContent('n8-7-v1-g2');
    expect(within(panel).getByRole('button', { name: 'Copy shortcode n8-7-v1-g2' })).toBeVisible();
    expect(within(panel).getByText('Second take')).toBeVisible();
    expect(within(panel).getByText('2:05')).toBeVisible();
    expect(within(panel).getByText('v5')).toBeVisible();
    expect(within(panel).getByText('4 of 5 stars')).toBeVisible();
    expect(within(panel).getByText('Active')).toBeVisible();
    expect(within(panel).getByRole('link', { name: 'Version 1' })).toBeVisible();
    expect(
      within(panel).getByRole('link', { name: 'Open in Suno: n8-7-v1-g2 (opens a new tab)' }),
    ).toHaveAttribute('href', 'https://suno.com/song/suno-n8-7-v1-g2');
    // The panel's Generation is marked in the table, and its Version is in the editor.
    expect(generationRow('n8-7-v1-g2')).toHaveAttribute('data-open', 'true');
    expect(screen.getByRole('heading', { level: 3, name: 'Version 1' })).toBeInTheDocument();

    await user.click(within(panel).getByRole('button', { name: 'Close' }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(router.state.location.pathname).toBe('/songs/n8-7/v/1');
    expect(server.writes).toEqual([]);
  });

  it('opens from a deep link to a hidden Generation, turning on what it takes to list it', async () => {
    const { server } = versionServer([ONE, testVersion('2', { archived: true })]);
    server.generations = [testGeneration('2', 1, { state: 'archived' })];

    const { router } = renderApp('/songs/n8-7/generations/N8-7-V2-G1');

    expect(await openedPanel('Generation n8-7-v2-g1')).toBeVisible();
    await waitFor(() => {
      expect(new URLSearchParams(router.state.location.search).get('archived')).toBe('1');
    });
    expect(new URLSearchParams(router.state.location.search).get('archivedGenerations')).toBe('1');
    await waitFor(() => {
      expect(listed()).toEqual(['1', '2']);
    });
    expect(generationsUnder('2')).toEqual(['n8-7-v2-g1']);
  });

  it('opens from a Generation shortcode given to the Go to box', async () => {
    const user = userEvent.setup();
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1), testGeneration('1', 2)];

    const { router } = await openSong();
    await user.type(screen.getByRole('textbox', { name: 'Go to' }), 'N8-7-V1-G2{Enter}');

    expect(await openedPanel('Generation n8-7-v1-g2')).toBeVisible();
    expect(router.state.location.pathname).toBe('/songs/n8-7/generations/n8-7-v1-g2');
  });

  it('says so in the panel when the Song has no such Generation', async () => {
    const { server } = versionServer([ONE]);
    server.generations = [testGeneration('1', 1)];

    renderApp('/songs/n8-7/generations/n8-7-v1-g9');

    const panel = await screen.findByRole('dialog', { name: 'Generation not found' });
    expect(within(panel).getByTestId('generation-not-found')).toHaveTextContent(
      'This Song has no Generation n8-7-v1-g9.',
    );
  });
});
