import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Dashboard } from '../api/dashboard';
import type { DashboardLayout, SectionPlacement } from '../api/dashboardSettings';
import { DEFAULT_SECTION_ORDER } from '../api/dashboardSettings';
import { healthyReport, jsonResponse, renderApp, requestPath, stubFetch } from '../test/helpers';
import { movedText, moveSection } from './customizeRules';

const WRITING = { id: '01a10a6e-dc81-7001-8000-000000000002', name: 'Writing', colour: 'blue' };

const DASHBOARD: Dashboard = {
  recentlyEdited: { data: { songs: [], total: 0 } },
  workflowStates: { data: { states: [{ ...WRITING, hidden: false, songCount: 2 }] } },
  withoutSelection: { data: { count: 0, songs: [] } },
  unmatchedFiles: { data: { count: 0, mediaUnavailable: false } },
  sunoReviews: { data: { count: 0, exports: [] } },
  sunoProblems: { data: { count: 0, problems: [] } },
};

const shownDefault = (): SectionPlacement[] =>
  DEFAULT_SECTION_ORDER.map((key) => ({ key, hidden: false }));

function layout(sections: SectionPlacement[], revision = 0, customized = false): DashboardLayout {
  return { revision, customized, sections, defaultOrder: [...DEFAULT_SECTION_ORDER] };
}

/**
 * A fake n8Tracks with the dashboard and its arrangement: `server.layout` is what is stored; each
 * write is in `server.writes`; `server.conflict` makes the next write stale.
 */
function serve(initial: DashboardLayout = layout(shownDefault())) {
  const server = {
    layout: initial,
    reads: 0,
    writes: [] as { method: string; ifMatch: string | null; body: unknown }[],
    conflict: false,
  };
  stubFetch().mockImplementation((input, init) => {
    const path = requestPath(input);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (path.endsWith('/health')) {
      return Promise.resolve(jsonResponse(200, healthyReport));
    }
    if (path.endsWith('/api/v1/dashboard')) {
      return Promise.resolve(jsonResponse(200, DASHBOARD));
    }
    if (path.endsWith('/api/v1/settings/dashboard')) {
      if (method === 'GET') {
        server.reads += 1;
        return Promise.resolve(jsonResponse(200, server.layout));
      }
      const ifMatch = new Headers(init?.headers).get('If-Match');
      const body = typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined;
      server.writes.push({ method, ifMatch, body });
      if (server.conflict) {
        server.layout = layout(shownDefault(), server.layout.revision + 5, false);
        return Promise.resolve(
          jsonResponse(409, { code: 'revision_conflict', current: server.layout }),
        );
      }
      const sections =
        method === 'DELETE' ? shownDefault() : (body as { sections: SectionPlacement[] }).sections;
      server.layout = layout(sections, server.layout.revision + 1, method !== 'DELETE');
      return Promise.resolve(jsonResponse(200, server.layout));
    }
    if (path.endsWith('/api/v1/settings/last-song')) {
      return Promise.resolve(jsonResponse(200, { song: null, deleted: false }));
    }
    return Promise.resolve(jsonResponse(404, { code: 'not_found' }));
  });
  return server;
}

/** The dashboard sections shown, in order, by key. */
function shownSections(): string[] {
  return screen
    .queryAllByTestId(/^dashboard-section-/)
    .map((section) => section.dataset.testid?.replace('dashboard-section-', '') ?? '');
}

/** The sections in the Customize dialog, in order, by key. */
function draftOrder(): string[] {
  return screen.getAllByTestId('customize-section').map((row) => row.dataset.section ?? '');
}

async function openCustomize(user: ReturnType<typeof userEvent.setup>) {
  const button = await screen.findByRole('button', { name: 'Customize' });
  await waitFor(() => {
    expect(button).toBeEnabled();
  });
  await user.click(button);
  return screen.findByRole('dialog', { name: 'Customize the dashboard' });
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('the arrangement rules', () => {
  it('moves a section, and leaves the arrangement as it is at the ends', () => {
    const sections = shownDefault();
    expect(moveSection(sections, 1, 0).map((section) => section.key)).toEqual([
      'unmatchedFiles',
      'recentlyEdited',
      'sunoReviews',
      'sunoProblems',
      'workflowStates',
      'withoutSelection',
    ]);
    expect(moveSection(sections, 0, -1)).toBe(sections);
    expect(moveSection(sections, 5, 6)).toBe(sections);
    expect(movedText('Unmatched Files', 1, 6)).toBe('Unmatched Files moved to position 1 of 6.');
  });
});

describe('customizing the dashboard', () => {
  it('shows the sections in the saved order, leaving out the hidden ones; quick actions always show', async () => {
    serve(
      layout(
        [
          { key: 'sunoProblems', hidden: false },
          { key: 'recentlyEdited', hidden: true },
          { key: 'workflowStates', hidden: false },
          { key: 'unmatchedFiles', hidden: true },
          { key: 'sunoReviews', hidden: false },
          { key: 'withoutSelection', hidden: false },
        ],
        3,
        true,
      ),
    );

    renderApp('/');

    await waitFor(() => {
      expect(shownSections()).toEqual([
        'sunoProblems',
        'workflowStates',
        'sunoReviews',
        'withoutSelection',
      ]);
    });
    expect(screen.getByRole('region', { name: 'Quick actions' })).toBeVisible();
  });

  it('hides a section and moves another to the top with its buttons, announcing each move, and saves', async () => {
    const server = serve();
    const user = userEvent.setup();
    renderApp('/');
    await waitFor(() => {
      expect(shownSections()).toEqual([...DEFAULT_SECTION_ORDER]);
    });

    const dialog = await openCustomize(user);
    await user.click(within(dialog).getByRole('checkbox', { name: 'By workflow state' }));
    await user.click(within(dialog).getByRole('button', { name: 'Move Unmatched Files up' }));

    expect(draftOrder().slice(0, 2)).toEqual(['unmatchedFiles', 'recentlyEdited']);
    expect(screen.getByTestId('customize-announcement')).toHaveTextContent(
      'Unmatched Files moved to position 1 of 6.',
    );
    // Nothing is saved, nor shown, until Save.
    expect(server.writes).toEqual([]);
    expect(shownSections()[0]).toBe('recentlyEdited');

    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(
        screen.queryByRole('dialog', { name: 'Customize the dashboard' }),
      ).not.toBeInTheDocument();
    });
    expect(server.writes).toEqual([
      {
        method: 'PUT',
        ifMatch: '"0"',
        body: {
          sections: [
            { key: 'unmatchedFiles', hidden: false },
            { key: 'recentlyEdited', hidden: false },
            { key: 'sunoReviews', hidden: false },
            { key: 'sunoProblems', hidden: false },
            { key: 'workflowStates', hidden: true },
            { key: 'withoutSelection', hidden: false },
          ],
        },
      },
    ]);
    expect(shownSections()).toEqual([
      'unmatchedFiles',
      'recentlyEdited',
      'sunoReviews',
      'sunoProblems',
      'withoutSelection',
    ]);
  });

  it('reorders by keyboard: up, down, and at the ends focus moves to the button that still works', async () => {
    serve();
    const user = userEvent.setup();
    renderApp('/');
    const dialog = await openCustomize(user);
    const announcement = screen.getByTestId('customize-announcement');

    // Move down from the top with the keyboard.
    within(dialog).getByRole('button', { name: 'Move Recently edited down' }).focus();
    await user.keyboard('{Enter}');
    expect(draftOrder().slice(0, 2)).toEqual(['unmatchedFiles', 'recentlyEdited']);
    expect(announcement).toHaveTextContent('Recently edited moved to position 2 of 6.');
    expect(within(dialog).getByRole('button', { name: 'Move Recently edited down' })).toHaveFocus();

    // Back up to the top: Move up is then disabled, so focus is on Move down.
    await user.tab({ shift: true });
    expect(within(dialog).getByRole('button', { name: 'Move Recently edited up' })).toHaveFocus();
    await user.keyboard(' ');
    expect(draftOrder()[0]).toBe('recentlyEdited');
    expect(announcement).toHaveTextContent('Recently edited moved to position 1 of 6.');
    expect(within(dialog).getByRole('button', { name: 'Move Recently edited up' })).toBeDisabled();
    expect(within(dialog).getByRole('button', { name: 'Move Recently edited down' })).toHaveFocus();

    // At the other end: the last section's Move down is disabled.
    const last = 'Without a Selected Generation';
    expect(within(dialog).getByRole('button', { name: `Move ${last} down` })).toBeDisabled();
    within(dialog).getByRole('button', { name: 'Move By workflow state down' }).focus();
    await user.keyboard('{Enter}');
    expect(draftOrder().slice(-2)).toEqual(['withoutSelection', 'workflowStates']);
    expect(announcement).toHaveTextContent('By workflow state moved to position 6 of 6.');
    expect(within(dialog).getByRole('button', { name: 'Move By workflow state up' })).toHaveFocus();

    // Each drag handle is a keyboard control too, named for its section.
    expect(within(dialog).getByRole('button', { name: 'Drag Recently edited' })).toBeVisible();
  });

  it('resets the draft to the default, and Save then clears the stored arrangement', async () => {
    const server = serve(
      layout(
        [
          { key: 'withoutSelection', hidden: true },
          ...shownDefault().filter((section) => section.key !== 'withoutSelection'),
        ],
        4,
        true,
      ),
    );
    const user = userEvent.setup();
    renderApp('/');
    await waitFor(() => {
      expect(shownSections()).not.toContain('withoutSelection');
    });

    const dialog = await openCustomize(user);
    expect(draftOrder()[0]).toBe('withoutSelection');
    await user.click(within(dialog).getByRole('button', { name: 'Reset' }));
    expect(draftOrder()).toEqual([...DEFAULT_SECTION_ORDER]);
    expect(
      within(dialog).getByRole('checkbox', { name: 'Without a Selected Generation' }),
    ).toBeChecked();
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(shownSections()).toEqual([...DEFAULT_SECTION_ORDER]);
    });
    expect(server.writes).toEqual([{ method: 'DELETE', ifMatch: '"4"', body: undefined }]);
  });

  it('discards the draft on Cancel, and on Close', async () => {
    const server = serve();
    const user = userEvent.setup();
    renderApp('/');

    let dialog = await openCustomize(user);
    await user.click(within(dialog).getByRole('checkbox', { name: 'Recently edited' }));
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => {
      expect(
        screen.queryByRole('dialog', { name: 'Customize the dashboard' }),
      ).not.toBeInTheDocument();
    });

    expect(server.writes).toEqual([]);
    expect(shownSections()).toContain('recentlyEdited');
    dialog = await openCustomize(user);
    expect(within(dialog).getByRole('checkbox', { name: 'Recently edited' })).toBeChecked();

    // The dialog's own close button is named (axe's button-name), and discards the draft too.
    await user.click(within(dialog).getByRole('checkbox', { name: 'Recently edited' }));
    await user.click(within(dialog).getByRole('button', { name: 'Close' }));
    await waitFor(() => {
      expect(
        screen.queryByRole('dialog', { name: 'Customize the dashboard' }),
      ).not.toBeInTheDocument();
    });
    expect(server.writes).toEqual([]);
    dialog = await openCustomize(user);
    expect(within(dialog).getByRole('checkbox', { name: 'Recently edited' })).toBeChecked();
  });

  it('asks to reload when the arrangement changed elsewhere, and Reload starts from it', async () => {
    const server = serve();
    server.conflict = true;
    const user = userEvent.setup();
    renderApp('/');

    const dialog = await openCustomize(user);
    await user.click(within(dialog).getByRole('checkbox', { name: 'Suno reviews' }));
    await user.click(within(dialog).getByRole('button', { name: 'Save' }));

    const conflict = await within(dialog).findByTestId('customize-conflict');
    expect(conflict).toHaveTextContent('The arrangement was changed in another tab or browser');
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled();

    server.conflict = false;
    const readsBefore = server.reads;
    await user.click(within(conflict).getByRole('button', { name: 'Reload the arrangement' }));
    await waitFor(() => {
      expect(screen.queryByTestId('customize-conflict')).not.toBeInTheDocument();
    });
    expect(server.reads).toBe(readsBefore + 1);
    // The draft starts again from what is stored now.
    expect(screen.getByRole('checkbox', { name: 'Suno reviews' })).toBeChecked();
  });

  it('says so, with Customize, when every section is hidden', async () => {
    serve(
      layout(
        shownDefault().map((section) => ({ ...section, hidden: true })),
        2,
        true,
      ),
    );
    const user = userEvent.setup();
    renderApp('/');

    const notice = await screen.findByTestId('dashboard-all-hidden');
    expect(notice).toHaveTextContent('Every dashboard section is hidden.');
    expect(shownSections()).toEqual([]);
    expect(screen.getByRole('region', { name: 'Quick actions' })).toBeVisible();

    await user.click(within(notice).getByRole('button', { name: 'Customize the dashboard' }));
    const dialog = await screen.findByRole('dialog', { name: 'Customize the dashboard' });
    expect(within(dialog).getByTestId('customize-all-hidden')).toHaveTextContent(
      'Every section is hidden: the dashboard will show only the quick actions.',
    );
  });

  it('shows the default arrangement when the arrangement cannot be read', async () => {
    serve();
    stubFetch().mockImplementation((input) => {
      const path = requestPath(input);
      if (path.endsWith('/health')) {
        return Promise.resolve(jsonResponse(200, healthyReport));
      }
      if (path.endsWith('/api/v1/dashboard')) {
        return Promise.resolve(jsonResponse(200, DASHBOARD));
      }
      return Promise.resolve(jsonResponse(500, { code: 'failed' }));
    });

    renderApp('/');

    await waitFor(() => {
      expect(shownSections()).toEqual([...DEFAULT_SECTION_ORDER]);
    });
  });
});
