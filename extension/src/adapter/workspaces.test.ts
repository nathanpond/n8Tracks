import { describe, expect, it } from 'vitest';
import { sunoFixture, sunoObject } from '../testing/sunoResponses.ts';
import { OBSERVER_SOURCE, observedKindOf, type ObservedMessage } from './observed.ts';
import {
  createdWorkspaceOf,
  namedLike,
  workspaceNameFor,
  workspaceOfFeed,
  workspaceOptions,
  WorkspaceList,
  workspaceRow,
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

describe('Suno’s workspace list (TS-003)', () => {
  it('is complete only when pages one to N hold num_total_results', () => {
    const list = new WorkspaceList();
    // The fixtures: pages 1 and 5 of a 96-workspace list.
    expect(list.add(observed('workspaces', sunoFixture('project-me.page-1.response')))).toBe(true);
    expect(list.complete).toBe(false);
    expect(list.add(observed('workspaces', sunoFixture('project-me.last-page.response')))).toBe(
      true,
    );
    // Page 5 is here, but 2 to 4 are not: still not complete.
    expect(list.pagesInOrder).toBe(1);
    expect(list.complete).toBe(false);
    for (const page of [2, 3, 4]) {
      list.add(listPage(page, 96, [`p${String(page)}`]));
    }
    expect(list.complete).toBe(true);
    expect(list.workspaces().map((workspace) => workspace.id)).toEqual([
      'default',
      '00000000-0000-4000-8000-000000000105',
      '00000000-0000-4000-8000-000000000125',
      'p2',
      'p3',
      'p4',
      '00000000-0000-4000-8000-000000000126',
      '00000000-0000-4000-8000-000000000127',
      '00000000-0000-4000-8000-000000000128',
    ]);
  });

  it('takes a page that comes again in place of the first copy, and refuses what is not a page', () => {
    const list = new WorkspaceList();
    list.add(listPage(1, 2, ['a']));
    list.add(listPage(1, 2, ['a', 'b']));

    expect(list.complete).toBe(true);
    expect(list.workspaces().map((workspace) => workspace.id)).toEqual(['a', 'b']);
    expect(list.add(observed('workspaces', { projects: [] }))).toBe(false);
    expect(list.add(observed('playlists', { current_page: 1, num_total_results: 0 }))).toBe(false);
    // Complement: an empty list of none is complete at its first page.
    const empty = new WorkspaceList();
    empty.add(listPage(1, 0, []));
    expect(empty.complete).toBe(true);
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

describe('a workspace’s row and the choice offered', () => {
  it('finds the row by the whole name, written literally, and never by a blank one', () => {
    const row = workspaceRow('  Night   (Drive) ');
    expect(row?.name).toBeInstanceOf(RegExp);
    const name = row?.name as RegExp;
    expect(name.test('Night (Drive) 3 songs · 2d ago')).toBe(true);
    expect(name.test('Night (Drive)')).toBe(true);
    expect(name.test('Night Drive 3 songs')).toBe(false);
    expect(name.test('Night (Drive)s 3 songs')).toBe(false);
    expect(name.test('A Night (Drive) 3 songs')).toBe(false);
    expect(row?.description).toBe("the workspace's row in Suno's workspace list");
    expect(workspaceRow('   ')).toBeNull();
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
