// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { fakeClock, loadSnapshot } from '../../testing/snapshots.ts';
import { checkWorkflow } from '../registry.ts';
import { PrimitiveError } from '../primitives.ts';
import { runWorkflow } from '../workflow.ts';
import { askForMore, listOnPage, loadMore, SONG_ROW } from './loadMore.ts';

afterEach(() => {
  document.body.innerHTML = '';
});

describe('the load-more workflow', () => {
  it.each([
    ['library-list', 'https://suno.com/me'],
    ['library-trash', 'https://suno.com/me/trash'],
    ['playlist', 'https://suno.com/playlist/00000000-0000-4000-8000-00000000012b'],
    // The TS-003 workspace list has no snapshot; the Library page around it has Suno's navigation.
    ['library-list', 'https://suno.com/me/workspaces'],
    // TS-007-23: no rowgroup any more, only the rows (`role=group`) in plain wrappers.
    ['library-songs-groups', 'https://suno.com/me'],
    ['library-trash-groups', 'https://suno.com/me/trash'],
    ['playlist-groups', 'https://suno.com/playlist/00000000-0000-4000-8000-00000000012b'],
    ['library-workspaces', 'https://suno.com/me/workspaces'],
    ['create-workspace-songs-groups', 'https://suno.com/create?wid=w-1'],
  ])('finds the list on the %s snapshot at %s', (snapshot, address) => {
    const page = loadSnapshot(snapshot, address);

    expect(listOnPage(page)).toEqual({ ok: true });
    expect(checkWorkflow(loadMore, page).state).toBe('ready');
  });

  it('is not working where the list is missing, and not checked off a list page', () => {
    // The workspace list's page holds no song row.
    expect(listOnPage(loadSnapshot('library-workspaces', 'https://suno.com/playlist/x'))).toEqual({
      ok: false,
      expected: "Suno's list of songs",
    });
    expect(
      checkWorkflow(loadMore, loadSnapshot('library-list', 'https://suno.com/create')).state,
    ).toBe('not-checked');
  });
});

/** The pager's controls in a TS-007 snapshot: the container of the span reading "Page". */
function pagerControls(): { buttons: HTMLButtonElement[]; input: HTMLInputElement } {
  const label = [...document.querySelectorAll('span')].find(
    (span) => span.textContent === 'Page' && span.children.length === 0,
  );
  const container = label?.parentElement;
  if (container === null || container === undefined) {
    throw new Error('the snapshot has no pager');
  }
  const input = container.querySelector('input');
  if (input === null) {
    throw new Error('the pager has no page number');
  }
  return { buttons: [...container.querySelectorAll('button')], input };
}

/** Every click on the page, by the element clicked. */
function clicks(): Element[] {
  const clicked: Element[] = [];
  document.addEventListener(
    'click',
    (event) => {
      clicked.push(event.target as Element);
    },
    true,
  );
  return clicked;
}

describe('finding the rows of a song list (TS-007-23)', () => {
  it('takes a row by the song link it holds, not Suno’s empty placeholders', () => {
    const page = loadSnapshot('library-songs-groups', 'https://suno.com/me');
    const groups = document.querySelectorAll('[role="group"]');
    const placeholders = [...groups].filter((group) => group.querySelector('a') === null);
    // Three rows and two placeholders ("Untitled", nothing inside) are in the fixture.
    expect([groups.length, placeholders.length]).toEqual([5, 2]);
    expect(page.readAll(SONG_ROW)).toHaveLength(3);

    for (const link of document.querySelectorAll('a[href^="/song/"]')) {
      link.remove();
    }
    expect(page.readAll(SONG_ROW)).toEqual([]);
    expect(listOnPage(page)).toEqual({ ok: false, expected: "Suno's list of songs" });
  });

  it('finds the rows on the TS-003 and TS-007 snapshots too, where a rowgroup held them', () => {
    for (const snapshot of ['library-list', 'library-trash', 'library-songs-page-1', 'playlist']) {
      const page = loadSnapshot(snapshot, 'https://suno.com/me');
      expect(page.readAll(SONG_ROW).length, snapshot).toBeGreaterThan(0);
    }
  });
});

describe('asking for more where Suno shows its pager (TS-007)', () => {
  it.each([
    ['library-songs-page-1', 2],
    ['library-songs-page-2', 3],
    // TS-007-23: Library's pager is as TS-007 captured it.
    ['library-songs-groups', 2],
  ])('presses only the pager’s › on %s, the button after the page number', (snapshot, count) => {
    const page = loadSnapshot(snapshot, 'https://suno.com/me');
    const { buttons, input } = pagerControls();
    expect(buttons).toHaveLength(count);
    const clicked = clicks();

    askForMore(page);

    expect(clicked).toEqual([buttons[1]]);
    expect(input.nextElementSibling).toBe(buttons[1]);
    expect(page.refusal()).toBeNull();
  });

  it.each(['library-songs-page-1', 'library-songs-page-2', 'library-songs-groups'])(
    'runs as the load-more workflow on %s, pressing ›',
    async (snapshot) => {
      const page = loadSnapshot(snapshot, 'https://suno.com/me', fakeClock());
      const clicked = clicks();

      const result = await runWorkflow(loadMore, page, {}, { clock: fakeClock() });

      expect(result.ok).toBe(true);
      expect(clicked).toEqual([pagerControls().buttons[1]]);
    },
  );

  it('stops the workflow, naming the pager, when it does not look as captured', async () => {
    const page = loadSnapshot('library-songs-page-1', 'https://suno.com/me', fakeClock());
    pagerControls().input.remove();

    const result = await runWorkflow(loadMore, page, {}, { clock: fakeClock() });

    expect(result.ok).toBe(false);
    expect(result.ok ? '' : result.failure.expected).toMatch(/^one list pager/);
  });

  it('scrolls, pressing nothing, where there is no pager (TS-003 snapshots)', () => {
    for (const [snapshot, address] of [
      ['library-list', 'https://suno.com/me'],
      ['library-trash', 'https://suno.com/me/trash'],
      ['playlist', 'https://suno.com/playlist/00000000-0000-4000-8000-00000000012b'],
      // TS-007-23: still no pager on the Trash, a playlist, or the workspace list.
      ['library-trash-groups', 'https://suno.com/me/trash'],
      ['playlist-groups', 'https://suno.com/playlist/00000000-0000-4000-8000-00000000012b'],
      ['library-workspaces', 'https://suno.com/me/workspaces'],
    ] as const) {
      const page = loadSnapshot(snapshot, address);
      const clicked = clicks();
      expect(page.pagerNext()).toEqual({ kind: 'none' });
      askForMore(page);
      expect(clicked).toEqual([]);
    }
  });

  it.each([
    [
      'a named button',
      () => {
        pagerControls().buttons[1]?.setAttribute('aria-label', 'Publish');
      },
    ],
    [
      'a button with text',
      () => {
        pagerControls().buttons[1]?.append('Next');
      },
    ],
    [
      'no page number',
      () => {
        pagerControls().input.remove();
      },
    ],
    [
      'something other than a button after ›',
      () => {
        pagerControls().buttons[1]?.after(document.createElement('a'));
      },
    ],
    [
      'a second pager',
      () => {
        const label = document.createElement('span');
        label.textContent = 'Page';
        document.body.append(label);
      },
    ],
  ])('fails closed, pressing nothing, on a pager with %s', (_what, change) => {
    const page = loadSnapshot('library-songs-page-1', 'https://suno.com/me');
    change();
    const clicked = clicks();

    expect(page.pagerNext().kind).toBe('mismatch');
    expect(() => {
      askForMore(page);
    }).toThrow(PrimitiveError);
    expect(clicked).toEqual([]);
  });

  it('presses no disabled ›', () => {
    const page = loadSnapshot('library-songs-page-1', 'https://suno.com/me');
    pagerControls().buttons[1]?.setAttribute('disabled', '');
    const clicked = clicks();

    expect(() => {
      askForMore(page);
    }).toThrow(PrimitiveError);
    expect(clicked).toEqual([]);
  });
});

const WORKSPACE_SONGS = 'https://suno.com/create?wid=00000000-0000-4000-8000-000000000427';

/** Gives an element a scrolling box jsdom does not lay out. */
function scrolling(element: Element): HTMLElement {
  const box = element as HTMLElement;
  Object.defineProperty(box, 'scrollHeight', { value: 5000, configurable: true });
  Object.defineProperty(box, 'clientHeight', { value: 500, configurable: true });
  box.style.overflowY = 'auto';
  return box;
}

/**
 * The panes of the `/create?wid=` snapshot that scroll in Suno's page: the song list's own
 * scroller, and the pane holding Suno's Create form beside it.
 */
function createPanes(): { list: HTMLElement; form: HTMLElement } {
  const list = document.querySelector('.clip-browser-list-scroller');
  const create = document.querySelector('[aria-label="Create song"]');
  // The form's pane: the outermost ancestor of the Create button not holding the song list.
  let form: Element | null | undefined = create;
  while (form?.parentElement != null && !form.parentElement.contains(list)) {
    form = form.parentElement;
  }
  if (list === null || form == null) {
    throw new Error('the snapshot has no song list or Create form');
  }
  return { list: scrolling(list), form: scrolling(form) };
}

/** The pager in the header of a workspace's songs (TS-007-23): ‹, the page number, ›. */
function workspacePager(): { previous: Element; input: Element; next: Element } {
  const input = document.querySelector('input[aria-label="Current page number"]');
  const previous = input?.previousElementSibling;
  const next = input?.nextElementSibling;
  if (input == null || previous == null || next == null) {
    throw new Error('the snapshot has no pager above the songs');
  }
  return { previous, input, next };
}

describe("asking for more of a workspace's songs on /create?wid= (#422, TS-007-23)", () => {
  it('finds the song list on the snapshot, and only on a Create page opened for a workspace', () => {
    const page = loadSnapshot('create-workspace-songs-groups', WORKSPACE_SONGS);

    expect(listOnPage(page)).toEqual({ ok: true });
    expect(checkWorkflow(loadMore, page).state).toBe('ready');
    expect(
      checkWorkflow(
        loadMore,
        loadSnapshot('create-workspace-songs-groups', 'https://suno.com/create'),
      ).state,
    ).toBe('not-checked');
  });

  it("presses only the pager's Next page, scrolling neither the list nor the Create form", () => {
    const page = loadSnapshot('create-workspace-songs-groups', WORKSPACE_SONGS);
    const { list, form } = createPanes();
    const { next } = workspacePager();
    const clicked = clicks();

    askForMore(page);

    expect(clicked).toEqual([next]);
    expect(next.getAttribute('aria-label')).toBe('Next page');
    expect([list.scrollTop, form.scrollTop]).toEqual([0, 0]);
    expect(page.refusal()).toBeNull();
  });

  it('runs as the load-more workflow there, pressing Next page and nothing else', async () => {
    const page = loadSnapshot('create-workspace-songs-groups', WORKSPACE_SONGS, fakeClock());
    const { list } = createPanes();
    const clicked = clicks();

    const result = await runWorkflow(loadMore, page, {}, { clock: fakeClock() });

    expect(result.ok).toBe(true);
    expect(clicked).toEqual([workspacePager().next]);
    expect(list.scrollTop).toBe(0);
  });

  it('stops, pressing and scrolling nothing, where the list has no pager (the #422 snapshot)', async () => {
    const page = loadSnapshot('create-workspace-songs', WORKSPACE_SONGS, fakeClock());
    const list = scrolling(document.querySelector('.clip-browser-list-scroller') ?? document.body);
    const clicked = clicks();

    const result = await runWorkflow(loadMore, page, {}, { clock: fakeClock() });

    expect(result.ok).toBe(false);
    expect(result.ok ? '' : result.failure.expected).toMatch(/^Suno's pager/);
    expect(clicked).toEqual([]);
    expect(list.scrollTop).toBe(0);
  });

  it.each([
    [
      'a next button named otherwise',
      () => {
        workspacePager().next.setAttribute('aria-label', 'Publish');
      },
    ],
    [
      'a next button with text',
      () => {
        workspacePager().next.append('Next');
      },
    ],
    [
      'something more beside the page number',
      () => {
        workspacePager().next.after(document.createElement('button'));
      },
    ],
    [
      'the buttons the other way round',
      () => {
        const { previous, next } = workspacePager();
        next.after(previous);
      },
    ],
    [
      'a second pager',
      () => {
        const input = document.createElement('input');
        input.setAttribute('aria-label', 'Current page number');
        document.body.append(input);
      },
    ],
    [
      "Library's pager as well",
      () => {
        const header = document.createElement('div');
        header.innerHTML =
          '<span>Page</span><button type="button"><svg></svg></button><input placeholder="#" value="1"><button type="button"><svg></svg></button>';
        document.body.append(header);
      },
    ],
  ])('fails closed, pressing nothing, on a pager with %s', (_what, change) => {
    const page = loadSnapshot('create-workspace-songs-groups', WORKSPACE_SONGS);
    change();
    const clicked = clicks();

    expect(page.pagerNext().kind).toBe('mismatch');
    expect(() => {
      askForMore(page);
    }).toThrow(PrimitiveError);
    expect(clicked).toEqual([]);
  });

  it('presses no disabled Next page (the last page)', () => {
    const page = loadSnapshot('create-workspace-songs-groups', WORKSPACE_SONGS);
    workspacePager().next.setAttribute('disabled', '');
    const clicked = clicks();

    expect(() => {
      askForMore(page);
    }).toThrow(PrimitiveError);
    expect(clicked).toEqual([]);
  });

  it('stops, pressing nothing, when the page shows no song list', async () => {
    const page = loadSnapshot('create-workspace-songs-groups', WORKSPACE_SONGS, fakeClock());
    for (const row of document.querySelectorAll('[role="group"]')) {
      row.remove();
    }
    const clicked = clicks();

    const result = await runWorkflow(loadMore, page, {}, { clock: fakeClock() });

    expect(result.ok).toBe(false);
    expect(result.ok ? '' : result.failure.expected).toBe("Suno's list of songs");
    expect(clicked).toEqual([]);
  });
});
