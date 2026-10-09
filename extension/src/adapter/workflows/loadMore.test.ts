// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { fakeClock, loadSnapshot } from '../../testing/snapshots.ts';
import { checkWorkflow } from '../registry.ts';
import { PrimitiveError } from '../primitives.ts';
import { runWorkflow } from '../workflow.ts';
import { askForMore, listOnPage, loadMore } from './loadMore.ts';

afterEach(() => {
  document.body.innerHTML = '';
});

describe('the load-more workflow', () => {
  it.each([
    ['library-list', 'https://suno.com/me'],
    ['library-trash', 'https://suno.com/me/trash'],
    ['playlist', 'https://suno.com/playlist/00000000-0000-4000-8000-00000000012b'],
    // The workspace list has no snapshot; the Library page around it has Suno's navigation.
    ['library-list', 'https://suno.com/me/workspaces'],
  ])('finds the list on the %s snapshot at %s', (snapshot, address) => {
    const page = loadSnapshot(snapshot, address);

    expect(listOnPage(page)).toEqual({ ok: true });
    expect(checkWorkflow(loadMore, page).state).toBe('ready');
  });

  it('is not working where the list is missing, and not checked off a list page', () => {
    expect(listOnPage(loadSnapshot('library-trash', 'https://suno.com/playlist/x'))).toEqual({
      ok: false,
      expected: "the playlist's list of songs, named by its count",
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

describe('asking for more where Suno shows its pager (TS-007)', () => {
  it.each([
    ['library-songs-page-1', 2],
    ['library-songs-page-2', 3],
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

  it.each(['library-songs-page-1', 'library-songs-page-2'])(
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
