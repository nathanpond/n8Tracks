import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router';
import { describe, expect, it } from 'vitest';
import { App } from '../App';
import { healthyReport, jsonResponse, requestPath, stubFetch } from '../test/helpers';

function Location() {
  const { pathname, search } = useLocation();
  return <output data-testid="location">{`${pathname}${search}`}</output>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <App />
      <Location />
    </MemoryRouter>,
  );
}

function sidebar(): HTMLElement {
  return screen.getByRole('navigation', { name: 'Main' });
}

describe('the signed-in shell', () => {
  it('has a sidebar listing Songs, and Settings with Account, Credentials, Workflow, Genres, Suno, Backups, and System', async () => {
    stubFetch().mockImplementation(() => Promise.resolve(jsonResponse(200, healthyReport)));

    renderAt('/songs');

    expect(await screen.findByRole('heading', { level: 2, name: 'Songs' })).toBeVisible();
    const links = within(sidebar()).getAllByRole('link');
    expect(links.map((link) => link.textContent)).toEqual([
      'Songs',
      'Account',
      'Credentials',
      'Workflow',
      'Genres',
      'Suno',
      'Backups',
      'System',
    ]);
    expect(within(sidebar()).getByRole('group', { name: 'Settings' })).toBeInTheDocument();
    expect(within(sidebar()).getByRole('link', { name: 'Songs' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    expect(screen.getByRole('heading', { level: 1, name: 'n8Tracks' })).toBeVisible();
    expect(screen.getByTestId('user-menu')).toHaveTextContent('owner');
  });

  it('goes to each page from the sidebar and marks it as the current one', async () => {
    stubFetch().mockImplementation(() => Promise.resolve(jsonResponse(200, healthyReport)));
    const user = userEvent.setup();

    renderAt('/songs');
    await screen.findByRole('heading', { name: 'Songs' });

    await user.click(within(sidebar()).getByRole('link', { name: 'Account' }));
    expect(await screen.findByRole('heading', { level: 2, name: 'Account' })).toBeVisible();
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/settings\/account$/);
    expect(within(sidebar()).getByRole('link', { name: 'Account' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    expect(within(sidebar()).getByRole('link', { name: 'Songs' })).not.toHaveAttribute(
      'aria-current',
    );

    await user.click(within(sidebar()).getByRole('link', { name: 'System' }));
    expect(await screen.findByRole('heading', { level: 2, name: 'System' })).toBeVisible();
    expect(await screen.findByTestId('version')).toHaveTextContent('0.1.0');
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/settings\/system$/);

    await user.click(within(sidebar()).getByRole('link', { name: 'Songs' }));
    expect(await screen.findByRole('heading', { level: 2, name: 'Songs' })).toBeVisible();
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/songs$/);
  });

  it.each([
    ['/', '/songs', 'Songs'],
    ['/settings', '/settings/account', 'Account'],
  ])('sends %s on to %s', async (path, expected, heading) => {
    stubFetch().mockImplementation(() => Promise.resolve(jsonResponse(200, healthyReport)));

    renderAt(path);

    expect(await screen.findByRole('heading', { level: 2, name: heading })).toBeVisible();
    expect(screen.getByTestId('location')).toHaveTextContent(new RegExp(`^${expected}$`));
  });

  it('shows a path that is no page as not found, inside the shell', async () => {
    stubFetch().mockImplementation(() => Promise.resolve(jsonResponse(200, healthyReport)));
    const user = userEvent.setup();

    renderAt('/no/such/page');

    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeVisible();
    expect(sidebar()).toBeInTheDocument();
    await user.click(screen.getByRole('link', { name: 'Go to Songs' }));
    expect(await screen.findByRole('heading', { name: 'Songs' })).toBeVisible();
  });

  it('opens and closes the sidebar with the toggle, which says whether it is open', async () => {
    stubFetch().mockImplementation(() => Promise.resolve(jsonResponse(200, healthyReport)));
    const user = userEvent.setup();

    renderAt('/songs');
    const toggle = await screen.findByRole('button', { name: 'Open navigation' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(toggle).toHaveAttribute('aria-controls', sidebar().id);

    await user.click(toggle);
    expect(screen.getByRole('button', { name: 'Close navigation' })).toHaveAttribute(
      'aria-expanded',
      'true',
    );

    // Choosing a page closes it again.
    await user.click(within(sidebar()).getByRole('link', { name: 'System' }));
    expect(screen.getByRole('button', { name: 'Open navigation' })).toHaveAttribute(
      'aria-expanded',
      'false',
    );
  });
});

describe('the user menu', () => {
  it('shows the username and the two sign-out actions', async () => {
    stubFetch().mockImplementation(() => Promise.resolve(jsonResponse(200, healthyReport)));
    const user = userEvent.setup();

    renderAt('/songs');
    await user.click(await screen.findByTestId('user-menu'));

    const items = await screen.findAllByRole('menuitem');
    expect(items.map((item) => item.textContent)).toEqual(['Sign out', 'Sign out everywhere']);
  });

  it('"Sign out" ends this session and leaves the shell for the sign-in page', async () => {
    const mock = stubFetch();
    mock.mockImplementation((_input, init) =>
      Promise.resolve(
        init?.method === 'DELETE'
          ? new Response(null, { status: 204 })
          : jsonResponse(200, healthyReport),
      ),
    );
    const user = userEvent.setup();

    renderAt('/settings/account');
    await user.click(await screen.findByTestId('user-menu'));
    await user.click(await screen.findByRole('menuitem', { name: 'Sign out' }));

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeVisible();
    expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument();
    const call = mock.mock.calls.find(([, init]) => init?.method === 'DELETE');
    expect(call && requestPath(call[0])).toBe('/api/v1/session');
  });
});
