import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { jsonResponse, renderApp } from '../test/helpers';
import { baseSong, NIGHT, RUNNING, SUMMER, songServer } from '../test/songServer';

async function openTags(user: ReturnType<typeof userEvent.setup>) {
  renderApp('/songs/n8-7');
  await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' });
  await user.click(screen.getByRole('button', { name: 'Details' }));
  return screen.findByRole('combobox', { name: 'Tags' });
}

/** The Tags on the Song, by their remove buttons in the picker. */
function chosenTags() {
  return screen
    .queryAllByRole('button', { name: /^Remove Tag / })
    .map((button) => button.getAttribute('aria-label')?.replace('Remove Tag ', ''));
}

/** The Tag labels under the Song's title, as "name colour". */
function headerTags() {
  const group = screen.queryByRole('group', { name: 'Tags' });
  return group === null
    ? []
    : [...group.querySelectorAll('[data-tag-colour]')].map(
        (label) => `${label.textContent} ${label.getAttribute('data-tag-colour') ?? ''}`,
      );
}

describe('the Tags section of the Details panel', () => {
  it('suggests Tags by typing, each with its colour, and adds one on the Song’s revision', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const field = await openTags(user);

    await user.type(field, 'dri');
    const option = await screen.findByRole('option', { name: 'night drive' });
    expect(option.querySelector('[data-swatch]')).toHaveAttribute(
      'data-swatch',
      'var(--n8-state-teal)',
    );
    await user.keyboard('{Enter}');

    await waitFor(() => {
      expect(chosenTags()).toEqual(['night drive']);
    });
    expect(server.edits).toEqual([{ ifMatch: '"1"', body: { tagIds: [NIGHT.id] } }]);
    expect(server.song.revision).toBe(2);
    // Shown as a coloured label, named, on the Song page.
    expect(headerTags()).toEqual(['night drive teal']);
  });

  it('creates a Tag on the spot in the next unused colour and adds it', async () => {
    const user = userEvent.setup();
    const { server } = songServer({
      ...baseSong,
      tags: [{ id: RUNNING.id, name: RUNNING.name, colour: RUNNING.colour }],
    });
    const field = await openTags(user);

    await user.type(field, 'late summer');
    await user.click(await screen.findByRole('option', { name: 'Create Tag “late summer”' }));

    await waitFor(() => {
      expect(chosenTags()).toEqual(['late summer', 'running']);
    });
    expect(server.createdTags).toEqual(['late summer']);
    const created = server.tags.find((tag) => tag.name === 'late summer');
    // gray, red, and teal are taken: pink is the first unused.
    expect(created?.colour).toBe('pink');
    expect(server.edits.at(-1)?.body).toEqual({ tagIds: [RUNNING.id, created?.id] });
    expect(headerTags()).toEqual(['late summer pink', 'running gray']);
  });

  it('adds the existing Tag when the typed name matches one in another letter case', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const field = await openTags(user);

    await user.type(field, 'SUMMER');

    // No create option: Summer is suggested instead.
    expect(await screen.findByRole('option', { name: 'Summer' })).toBeVisible();
    expect(screen.queryByRole('option', { name: /Create Tag/ })).not.toBeInTheDocument();
    await user.keyboard('{Enter}');
    await waitFor(() => {
      expect(chosenTags()).toEqual(['Summer']);
    });
    expect(server.createdTags).toEqual([]);
  });

  it('removes a Tag from the Song, keeping the others, and lists them alphabetically', async () => {
    const user = userEvent.setup();
    const { server } = songServer({
      ...baseSong,
      tags: [
        { id: SUMMER.id, name: SUMMER.name, colour: SUMMER.colour },
        { id: RUNNING.id, name: RUNNING.name, colour: RUNNING.colour },
        { id: NIGHT.id, name: NIGHT.name, colour: NIGHT.colour },
      ],
    });
    await openTags(user);
    expect(chosenTags()).toEqual(['night drive', 'running', 'Summer']);

    await user.click(screen.getByRole('button', { name: 'Remove Tag running' }));

    await waitFor(() => {
      expect(chosenTags()).toEqual(['night drive', 'Summer']);
    });
    expect(server.edits.at(-1)?.body).toEqual({ tagIds: [SUMMER.id, NIGHT.id] });
  });

  it('merges its change onto Tags changed elsewhere without asking', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const field = await openTags(user);
    server.changeElsewhere({ tags: [{ id: RUNNING.id, name: RUNNING.name, colour: 'gray' }] });

    await user.type(field, 'summer');
    await user.click(await screen.findByRole('option', { name: 'Summer' }));

    await waitFor(() => {
      expect(chosenTags()).toEqual(['running', 'Summer']);
    });
    expect(server.edits.map((edit) => edit.ifMatch)).toEqual(['"1"', '"2"']);
    expect(server.edits[1]?.body).toEqual({ tagIds: [RUNNING.id, SUMMER.id] });
    expect(screen.queryByRole('dialog', { name: /changed/ })).not.toBeInTheDocument();
  });

  it('reads the Song and the list again when a Tag chosen no longer exists', async () => {
    const user = userEvent.setup();
    const { server } = songServer();
    const field = await openTags(user);
    server.next = () =>
      jsonResponse(422, {
        code: 'validation_failed',
        errors: { tagIds: ['A Tag chosen no longer exists. Choose again.'] },
      });

    await user.type(field, 'summer');
    await user.click(await screen.findByRole('option', { name: 'Summer' }));

    expect(
      await screen.findByText(
        'A Tag chosen no longer exists. Choose again. The Tags have been read again.',
      ),
    ).toBeVisible();
    expect(chosenTags()).toEqual([]);
  });

  it('shows the Tags under the title only when the Song has some', async () => {
    songServer();
    renderApp('/songs/n8-7');
    await screen.findByRole('heading', { level: 2, name: 'Running in a Pack' });

    expect(screen.queryByRole('group', { name: 'Tags' })).not.toBeInTheDocument();
  });

  it('names every Tag in its label, whatever its colour', async () => {
    songServer({
      ...baseSong,
      tags: [
        { id: RUNNING.id, name: RUNNING.name, colour: 'gray' },
        { id: SUMMER.id, name: SUMMER.name, colour: 'gray' },
      ],
    });
    renderApp('/songs/n8-7');

    const group = await screen.findByRole('group', { name: 'Tags' });
    // Two Tags of one colour are still told apart by name.
    expect(within(group).getByText('running')).toBeVisible();
    expect(within(group).getByText('Summer')).toBeVisible();
  });
});
