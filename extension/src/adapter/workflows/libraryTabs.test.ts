// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { fakeClock, loadSnapshot } from '../../testing/snapshots.ts';
import { checkWorkflow } from '../registry.ts';
import { runWorkflow } from '../workflow.ts';
import { openWorkspaceList } from './libraryTabs.ts';

afterEach(() => {
  document.body.innerHTML = '';
});

/** Every click on the page, by the element clicked; the tab's own navigation is stopped. */
function clicks(): Element[] {
  const clicked: Element[] = [];
  document.addEventListener(
    'click',
    (event) => {
      event.preventDefault();
      clicked.push(event.target as Element);
    },
    { capture: true },
  );
  return clicked;
}

describe("opening Library's Workspaces tab (#421)", () => {
  it.each(['library-list', 'library-songs-page-1', 'library-songs-groups'])(
    'presses the Workspaces tab, and nothing else, on the %s snapshot',
    async (snapshot) => {
      const clock = fakeClock();
      const page = loadSnapshot(snapshot, 'https://suno.com/me', clock);
      const clicked = clicks();

      expect(checkWorkflow(openWorkspaceList, page).state).toBe('ready');
      const result = await runWorkflow(openWorkspaceList, page, {}, { clock });

      expect(result.ok).toBe(true);
      expect(clicked).toHaveLength(1);
      expect(clicked[0]?.getAttribute('role')).toBe('tab');
      expect(clicked[0]?.textContent).toBe('Workspaces');
      expect(clicked[0]?.getAttribute('href')).toBe('/me/workspaces');
    },
  );

  it('fails closed, pressing nothing, when the tab is missing or not the only one', async () => {
    for (const change of [
      (tab: Element) => {
        tab.remove();
      },
      (tab: Element) => {
        tab.after(tab.cloneNode(true));
      },
      // A tab reading Workspaces that leads elsewhere is not it.
      (tab: Element) => {
        tab.setAttribute('href', '/me/workspaces/other');
      },
    ]) {
      const clock = fakeClock();
      const page = loadSnapshot('library-list', 'https://suno.com/me', clock);
      const tab = [...document.querySelectorAll('[role="tab"]')].find(
        (element) => element.textContent === 'Workspaces',
      );
      if (tab === undefined) {
        throw new Error('the snapshot has no Workspaces tab');
      }
      change(tab);
      const clicked = clicks();

      const result = await runWorkflow(openWorkspaceList, page, {}, { clock });

      expect(result.ok).toBe(false);
      expect(result.ok ? null : result.failure.step).toBe('workspaces tab');
      expect(clicked).toEqual([]);
    }
  });

  it('starts only on Library (/me)', () => {
    expect(
      checkWorkflow(openWorkspaceList, loadSnapshot('library-list', 'https://suno.com/create'))
        .state,
    ).toBe('not-checked');
  });
});
