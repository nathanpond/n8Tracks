import { describe, expect, it } from 'vitest';
import { sunoFixture, sunoObject } from '../testing/sunoResponses.ts';
import { OBSERVER_SOURCE, observedKindOf, type ObservedMessage } from './observed.ts';
import {
  createdWorkspaceOf,
  KnownWorkspaces,
  nameHolds,
  namedLike,
  workspaceNameFor,
  workspaceOfFeed,
  workspaceOptions,
  type ListedWorkspace,
} from './workspaces.ts';

function observed(
  kind: ObservedMessage['kind'],
  body: unknown,
  filters: unknown = null,
): ObservedMessage {
  return {
    source: OBSERVER_SOURCE,
    type: 'observed',
    kind,
    request: { cursor: null, page: null, filters, feedId: null },
    body,
  };
}

/** A page of the workspace list: page `page` of `total`, with the projects given. */
function listPage(page: number, total: number, ids: string[]) {
  return observed('workspaces', {
    num_total_results: total,
    current_page: page,
    projects: ids.map((id) => ({ id, name: `Name ${id}` })),
  });
}

describe('Suno’s workspaces as Generate sees them (TS-003, TS-007-15)', () => {
  it('takes every list answer, each workspace once by ID, never asking for the whole list', () => {
    const known = new KnownWorkspaces();
    // Pages 1 and 2 of 101: what Suno loaded; page 3 never comes (only a trusted scroll asks for it).
    expect(known.add(listPage(1, 101, ['a', 'b']))).toBe(true);
    expect(known.add(listPage(2, 101, ['c']))).toBe(true);
    // A search's answer, which may repeat a workspace and bring one from a page not loaded.
    known.add(listPage(1, 2, ['b', 'z']));

    expect(known.workspaces().map((workspace) => workspace.id)).toEqual(['a', 'b', 'c', 'z']);
    expect(known.get('z')?.name).toBe('Name z');
    expect(known.get('missing')).toBeUndefined();
  });

  it('follows a rename in a later answer, and refuses what is not a list answer', () => {
    const known = new KnownWorkspaces();
    known.add(observed('workspaces', { projects: [{ id: 'a', name: 'Old' }] }));
    known.add(observed('workspaces', { projects: [{ id: 'a', name: 'New' }, { name: 'no id' }] }));

    expect(known.workspaces().map((workspace) => workspace.name)).toEqual(['New']);
    expect(known.add(observed('workspaces', { current_page: 1 }))).toBe(false);
    expect(known.add(observed('playlists', { projects: [] }))).toBe(false);
  });

  it('reads the TS-003 fixtures and the constructed search answer', () => {
    const known = new KnownWorkspaces();
    expect(known.add(observed('workspaces', sunoFixture('project-me.page-1.response')))).toBe(true);
    expect(known.add(observed('workspaces', sunoFixture('project-me.search.response')))).toBe(true);
    expect(known.workspaces().length).toBeGreaterThan(3);
  });

  it('reads the new workspace from Suno’s answer to creating it, which the observer forwards', () => {
    expect(
      observedKindOf('https://studio-api-prod.suno.com/api/project', 'POST', 'https://suno.com'),
    ).toBe('workspace-created');
    // Complement: the list is another request, and reading one project is not observed.
    expect(
      observedKindOf(
        'https://studio-api-prod.suno.com/api/project/me?page=1',
        'GET',
        'https://suno.com',
      ),
    ).toBe('workspaces');
    expect(
      observedKindOf('https://studio-api-prod.suno.com/api/project', 'GET', 'https://suno.com'),
    ).toBeNull();

    const created = createdWorkspaceOf(
      observed('workspace-created', sunoFixture('project.create.response')),
    );
    expect(created).toMatchObject({
      id: '00000000-0000-4000-8000-000000000105',
      name: '<redacted 14 chars>',
    });
    expect(
      createdWorkspaceOf(observed('workspaces', sunoFixture('project.create.response'))),
    ).toBeNull();
    expect(createdWorkspaceOf(observed('workspace-created', { name: 'no id' }))).toBeNull();
  });

  it('knows which workspace the library pane asked for by its feed request’s filter', () => {
    const filters = (sunoObject('feed-v3.workspace-last-page.request') as { filters: unknown })
      .filters;

    expect(workspaceOfFeed(observed('library-feed', {}, filters))).toBe(
      '00000000-0000-4000-8000-000000000105',
    );
    const library = (sunoObject('feed-v3.library-page-1.request') as { filters: unknown }).filters;
    expect(workspaceOfFeed(observed('library-feed', {}, library))).toBeNull();
  });
});

describe('the choice offered', () => {
  it('narrows the offer to a search by name, without regard to case or spaces', () => {
    expect(nameHolds('Night Drive', ' night  dr')).toBe(true);
    expect(nameHolds('Night Drive', '')).toBe(true);
    expect(nameHolds('Night Drive', 'day')).toBe(false);
  });

  it('offers same-name workspaces with no Song first, then the others by name with their counts', () => {
    const listed: ListedWorkspace[] = [
      { id: 'w-1', name: 'Studio', raw: {} },
      { id: 'w-2', name: 'Night Drive', raw: {} },
      { id: 'w-3', name: 'night drive', raw: {} },
      { id: 'w-4', name: 'Night Drive', raw: {} },
      { id: 'w-5', name: 'Archive', raw: {} },
      { id: 'w-gone', name: 'Old', raw: {} },
    ];
    const counts = new Map([
      ['w-1', 3],
      ['w-4', 1],
    ]);

    const options = workspaceOptions(listed, 'Night Drive', counts, 'w-gone');

    expect(
      options.map(
        ({ id, sameName, songCount }) => `${id}:${String(sameName)}:${String(songCount)}`,
      ),
    ).toEqual([
      'w-2:true:0',
      // Same name but a Song in it already: listed with the others.
      'w-5:false:0',
      'w-3:false:0',
      'w-4:false:1',
      'w-1:false:3',
    ]);
    expect(namedLike(listed, ' Night  Drive ').map((workspace) => workspace.id)).toEqual([
      'w-2',
      'w-4',
    ]);
  });

  it('names a new workspace after the Song’s title, as Suno would show it', () => {
    expect(workspaceNameFor('  Night   Drive ')).toBe('Night Drive');
    expect(workspaceNameFor('   ')).toBe('Untitled');
  });
});
