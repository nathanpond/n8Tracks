// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { fakeClock, loadSnapshot } from '../testing/snapshots.ts';
import {
  findProblem,
  ForbiddenControlError,
  nameOf,
  Page,
  PANEL_HOST_ATTRIBUTE,
  PrimitiveError,
  StoppedError,
  type FindResult,
  type Found,
  SLIDER_KEY_SETTLE_MS,
  SLIDER_KEY_WAIT_MS,
  type Target,
} from './primitives.ts';
import type { Clock } from './clock.ts';

const ADVANCED = 'create-songs-advanced-more-options';

const STYLES: Target = {
  role: 'textbox',
  within: { testId: 'create-form-styles-wrapper', description: 'the Styles section' },
  description: 'a text box labelled Styles',
};
const WEIRDNESS: Target = {
  role: 'slider',
  name: 'Weirdness',
  description: 'the Weirdness slider',
};

function found(result: FindResult): Found {
  if (result.kind !== 'found') {
    throw new Error(`Expected to find it: ${findProblem(result)}`);
  }
  return result.found;
}

/** Every event of these types that reaches the document, in order. */
function recordEvents(...types: string[]): string[] {
  const seen: string[] = [];
  for (const type of types) {
    document.addEventListener(type, (event) => seen.push(event.type), { capture: true });
  }
  return seen;
}

afterEach(() => {
  document.body.innerHTML = '';
});

describe('find', () => {
  it('finds a control by role and accessible name in the Advanced snapshot', () => {
    const page = loadSnapshot(ADVANCED);

    const slider = found(page.find(WEIRDNESS));

    expect(page.read(slider)).toMatchObject({ value: '70', enabled: true });
  });

  it('finds the Styles box by role inside its test attribute', () => {
    const page = loadSnapshot(ADVANCED);

    const styles = found(page.find(STYLES));

    expect(page.read(styles).value).toBe('<redacted 20 chars>');
  });

  it('finds tabs by the name their text gives them', () => {
    const page = loadSnapshot(ADVANCED);

    const tabs = found(
      page.find({ role: 'tablist', name: 'What to create', description: 'the create tabs' }),
    );
    const speech = found(page.find({ role: 'tab', name: 'Speech', description: 'the Speech tab' }));

    expect(page.read(tabs).text).toContain('Songs');
    expect(page.read(speech).selected).toBe(false);
  });

  it('reports a missing element in the words of its target', () => {
    const page = loadSnapshot(ADVANCED);

    const result = page.find({ role: 'textbox', name: 'Nope', description: 'a Nope box' });

    expect(result).toEqual({
      kind: 'not_found',
      missing: { role: 'textbox', name: 'Nope', description: 'a Nope box' },
    });
  });

  it('fails as ambiguous on two matches instead of choosing the first', () => {
    const page = loadSnapshot(ADVANCED);
    const target: Target = {
      role: 'button',
      name: 'View saved style prompts',
      description: 'the saved style prompts button',
    };

    const result = page.find(target);

    expect(result).toEqual({ kind: 'ambiguous', target, count: 2 });
    expect(findProblem(result as Exclude<FindResult, { kind: 'found' }>)).toBe(
      'the saved style prompts button (found 2, so none was chosen)',
    );
  });

  it('fails when the region it must look inside is missing, naming the region', () => {
    const page = loadSnapshot('create-songs-simple');

    const result = page.find(STYLES);

    expect(result.kind).toBe('not_found');
    expect(findProblem(result as Exclude<FindResult, { kind: 'found' }>)).toBe(
      'the Styles section',
    );
  });

  it('ignores hidden elements', () => {
    const page = loadSnapshot(ADVANCED);
    const slider = found(page.find(WEIRDNESS));
    const element = document.querySelector('[aria-label="Weirdness"]');

    element?.parentElement?.setAttribute('style', 'display: none');
    expect(page.find(WEIRDNESS).kind).toBe('not_found');
    element?.parentElement?.removeAttribute('style');
    element?.parentElement?.setAttribute('aria-hidden', 'true');
    expect(page.find(WEIRDNESS).kind).toBe('not_found');
    element?.parentElement?.removeAttribute('aria-hidden');
    element?.closest('div[aria-hidden="false"]')?.setAttribute('hidden', '');
    expect(page.find(WEIRDNESS).kind).toBe('not_found');
    expect(slider.target).toBe(WEIRDNESS);
  });

  it('looks inside open shadow roots', () => {
    document.body.innerHTML = '<div id="host"></div>';
    const root = document.getElementById('host')?.attachShadow({ mode: 'open' });
    if (root === undefined) {
      throw new Error('no host');
    }
    root.innerHTML = '<button type="button">Inside</button>';
    const page = new Page(document);

    expect(
      page.find({ role: 'button', name: 'Inside', description: 'the inside button' }).kind,
    ).toBe('found');
  });

  it("never finds the extension's own panel, so a Close there cannot make Suno's ambiguous", () => {
    const page = loadSnapshot('download-dialog');
    const before = page.find({ role: 'button', name: 'Close', description: 'Close' });
    const host = document.createElement('n8tracks-panel');
    host.setAttribute(PANEL_HOST_ATTRIBUTE, '');
    host.attachShadow({ mode: 'open' }).innerHTML = '<button type="button">Close</button>';
    host.append(document.createElement('button'));
    document.body.append(host);

    const after = page.find({ role: 'button', name: 'Close', description: 'Close' });

    expect(after.kind).toBe(before.kind);
  });

  it('finds the match at a named place among several only when the target names it (#438)', () => {
    document.body.innerHTML =
      '<button type="button">Select</button><button type="button" id="b">Select</button>';
    const page = new Page(document);
    const select: Target = { role: 'button', name: 'Select', description: 'a Select' };

    const pressed: string[] = [];
    document.addEventListener('click', (event) => pressed.push((event.target as Element).id));

    expect(page.find(select)).toMatchObject({ kind: 'ambiguous', count: 2 });
    page.click(found(page.find({ ...select, nth: 1 })));
    expect(pressed).toEqual(['b']);
    expect(page.find({ ...select, nth: 2 })).toEqual({
      kind: 'not_found',
      missing: { ...select, nth: 2 },
    });
  });
});

describe('accessible names', () => {
  it('reads aria-labelledby, a label element, aria-label, content, title, and placeholder', () => {
    document.body.innerHTML = `
      <span id="a">From</span><span id="b">elsewhere</span>
      <input id="one" aria-labelledby="a b" />
      <label for="two">Labelled</label><input id="two" />
      <button id="three" aria-label="By label">ignored</button>
      <button id="four">By <svg aria-label="its"></svg> <span aria-hidden="true">x</span>content</button>
      <button id="five" title="By title"></button>
      <textarea id="six" placeholder="By placeholder"></textarea>`;

    const names = ['one', 'two', 'three', 'four', 'five', 'six'].map((id) => {
      const element = document.getElementById(id);
      return element === null ? null : nameOf(element);
    });

    expect(names).toEqual([
      'From elsewhere',
      'Labelled',
      'By label',
      'By its content',
      'By title',
      'By placeholder',
    ]);
  });

  it('names a hidden input without failing (its labels are null)', () => {
    document.body.innerHTML = '<input type="hidden" id="h" title="Hidden" />';
    const hidden = document.getElementById('h');

    expect(hidden && nameOf(hidden)).toBe('Hidden');
  });
});

describe('set', () => {
  it('sets a text box with the native setter and an input event, and reads it back', () => {
    const page = loadSnapshot(ADVANCED);
    const events = recordEvents('input', 'change', 'keydown');
    const styles = found(page.find(STYLES));

    page.set(styles, 'dark synthwave');

    expect(page.read(styles).value).toBe('dark synthwave');
    expect(events).toEqual(['input', 'change']);
  });

  it('moves a slider without an input by arrow keys, never Enter, until it reads the value', async () => {
    const clock = fakeClock();
    const page = loadSnapshot(ADVANCED, undefined, clock);
    const element = document.querySelector('[aria-label="Weirdness"]');
    const keys: string[] = [];
    element?.addEventListener('keydown', (event) => {
      const key = (event as KeyboardEvent).key;
      keys.push(key);
      const now = Number(element.getAttribute('aria-valuenow'));
      element.setAttribute('aria-valuenow', String(key === 'ArrowRight' ? now + 1 : now - 1));
    });
    const slider = found(page.find(WEIRDNESS));

    await page.slide(slider, 73);

    expect(page.read(slider).value).toBe('73');
    expect(keys).toEqual(['ArrowRight', 'ArrowRight', 'ArrowRight']);
    // Each key is read back a settle later (TS-007-9), and a key that took needs no longer wait.
    expect(clock.slept).toEqual([SLIDER_KEY_SETTLE_MS, SLIDER_KEY_SETTLE_MS, SLIDER_KEY_SETTLE_MS]);
  });

  it('leaves a slider that ignores keys as it was, after waiting for the one key, for the read-back to report', async () => {
    const clock = fakeClock();
    const page = loadSnapshot(ADVANCED, undefined, clock);
    const keys = recordEvents('keydown');
    const slider = found(page.find(WEIRDNESS));

    await page.slide(slider, 40);

    expect(page.read(slider).value).toBe('70');
    expect(keys).toEqual(['keydown']);
    expect(clock.slept.reduce((sum, ms) => sum + ms, 0)).toBe(SLIDER_KEY_WAIT_MS);
  });

  it('refuses to type into a slider: it is moved with slide', () => {
    const page = loadSnapshot(ADVANCED);
    const slider = found(page.find(WEIRDNESS));

    expect(() => {
      page.set(slider, 40);
    }).toThrow('the Weirdness slider to take a typed value (it is a slider)');
  });

  it('sets a slider through its range input when it has one', async () => {
    document.body.innerHTML =
      '<div role="slider" aria-label="Level" aria-valuenow="1"><input type="range" min="0" max="10" value="1" /></div>';
    const page = new Page(document);
    const events = recordEvents('input');
    const slider = found(page.find({ role: 'slider', name: 'Level', description: 'Level' }));

    await page.slide(slider, 6);

    expect(document.querySelector('input')?.value).toBe('6');
    expect(events).toEqual(['input']);
  });

  it('refuses a disabled control, an element that left the page, and anything after the run stopped', () => {
    const page = loadSnapshot(ADVANCED);
    const styles = found(page.find(STYLES));
    const textarea = document.querySelector('[data-testid="create-form-styles-wrapper"] textarea');

    textarea?.setAttribute('disabled', '');
    expect(() => {
      page.set(styles, 'x');
    }).toThrow(PrimitiveError);
    textarea?.removeAttribute('disabled');

    const stopped = new AbortController();
    stopped.abort();
    expect(() => {
      page.withSignal(stopped.signal).set(styles, 'x');
    }).toThrow(StoppedError);

    textarea?.remove();
    expect(() => {
      page.set(styles, 'x');
    }).toThrow('a text box labelled Styles to be still on the page');
  });

  it('refuses to type into something that is not a text box or slider', () => {
    const page = loadSnapshot(ADVANCED);
    const tab = found(page.find({ role: 'tab', name: 'Speech', description: 'the Speech tab' }));

    expect(() => {
      page.set(tab, 'x');
    }).toThrow('the Speech tab to take a typed value');
  });
});

describe('choose and click', () => {
  it('chooses a tab inside the tab list by clicking it, with the pointer events first', () => {
    const page = loadSnapshot(ADVANCED);
    const events = recordEvents('pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click');
    const clicked: (string | null)[] = [];
    document.addEventListener('click', (event) => {
      clicked.push((event.target as Element).textContent);
    });
    const tabs = found(
      page.find({ role: 'tablist', name: 'What to create', description: 'the create tabs' }),
    );

    page.choose(tabs, 'Speech');

    expect(events).toEqual(['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click']);
    expect(clicked).toEqual(['Speech']);
  });

  it('refuses an option the element does not offer, clicking nothing', () => {
    const page = loadSnapshot(ADVANCED);
    const events = recordEvents('click');
    const tabs = found(
      page.find({ role: 'tablist', name: 'What to create', description: 'the create tabs' }),
    );

    expect(() => {
      page.choose(tabs, 'Videos');
    }).toThrow('the create tabs to offer the option asked for');
    expect(events).toEqual([]);
  });

  it('chooses an option of a select by its text', () => {
    document.body.innerHTML =
      '<label for="s">Key</label><select id="s"><option value="a">A</option><option value="b">B</option></select>';
    const page = new Page(document);
    const select = found(page.find({ role: 'combobox', name: 'Key', description: 'the Key' }));

    page.choose(select, 'B');

    expect(page.read(select).value).toBe('b');
  });

  it('clicks a found button once', () => {
    const page = loadSnapshot(ADVANCED);
    const events = recordEvents('click');
    const button = found(
      page.find({ role: 'button', name: 'Clear styles', description: 'Clear styles' }),
    );

    page.click(button);

    expect(events).toEqual(['click']);
  });
});

describe('wait', () => {
  it('reads every 100 ms until the condition holds', async () => {
    const clock = fakeClock();
    const page = loadSnapshot(ADVANCED, undefined, clock);
    let reads = 0;

    const held = await page.wait(() => {
      reads += 1;
      return reads === 3;
    }, 1000);

    expect(held).toBe(true);
    expect(clock.slept).toEqual([100, 100]);
  });

  it('gives up at the timeout', async () => {
    const clock = fakeClock();
    const page = loadSnapshot(ADVANCED, undefined, clock);

    expect(await page.wait(() => false, 250)).toBe(false);
    expect(clock.slept).toEqual([100, 100, 50]);
  });
});

describe('the forbidden-control check (invariant 4)', () => {
  const CREATE: Target = { role: 'button', name: 'Create song', description: 'the Create button' };

  it('refuses to click Create before any event, and the handle then changes nothing more', () => {
    const page = loadSnapshot('create-songs-simple');
    const events = recordEvents('pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click');
    const create = found(page.find(CREATE));
    const clear = found(
      page.find({ role: 'button', name: 'Add Voice', description: 'the Add Voice button' }),
    );

    expect(() => {
      page.click(create);
    }).toThrow(ForbiddenControlError);
    expect(events).toEqual([]);
    expect(page.refusal()).toMatchObject({
      control: 'the Create button',
      reason: 'it is the Create button (Songs and Sounds)',
    });
    // There is no override: an allowed control on the same handle is refused too.
    expect(() => {
      page.click(clear);
    }).toThrow(ForbiddenControlError);
    expect(events).toEqual([]);
  });

  it('refuses to choose a forbidden menu item, the same check behind choose', () => {
    const page = loadSnapshot('clip-remix-menu');
    const events = recordEvents('click');
    const cover = found(
      page.find({ role: 'menuitem', name: 'Cover', description: 'the Cover item' }),
    );
    const group = document.querySelector(
      '[role="menuitem"][aria-label="Move to Trash"]',
    )?.parentElement;
    group?.setAttribute('role', 'menu');
    group?.setAttribute('aria-label', 'Danger');
    const danger = found(page.find({ role: 'menu', name: 'Danger', description: 'the menu' }));

    expect(() => {
      page.choose(danger, 'Move to Trash');
    }).toThrow(ForbiddenControlError);
    expect(events).toEqual([]);
    expect(page.read(cover).enabled).toBe(true);
  });

  it('clicks the create-workspace controls only through their own primitive', () => {
    const page = loadSnapshot('create-workspace-dialog');
    const events = recordEvents('click');
    const confirm = found(page.find({ role: 'button', name: 'Confirm', description: 'Confirm' }));

    expect(() => {
      new Page(document).click(confirm);
    }).toThrow(ForbiddenControlError);
    expect(events).toEqual([]);

    page.createWorkspaceClick(confirm);
    expect(events).toEqual(['click']);
  });

  it('refuses the create-workspace primitive on anything else, without poisoning the handle', () => {
    const page = loadSnapshot(ADVANCED);
    const events = recordEvents('click');
    const clear = found(
      page.find({ role: 'button', name: 'Clear styles', description: 'Clear styles' }),
    );

    expect(() => {
      page.createWorkspaceClick(clear);
    }).toThrow("Clear styles to be Suno's create-workspace control");
    expect(events).toEqual([]);
    expect(page.refusal()).toBeNull();
  });

  // #332: the matcher is asked before whether the control is enabled, so a forbidden control that
  // is disabled on the page is refused (the guard sees it), not a plain "to be enabled" stop.
  it.each([
    [
      'click',
      (page: Page, create: Found) => {
        page.click(create);
      },
    ],
    [
      'choose',
      (page: Page, create: Found) => {
        page.choose(create, 'Create song');
      },
    ],
    [
      'createWorkspaceClick',
      (page: Page, create: Found) => {
        page.createWorkspaceClick(create);
      },
    ],
  ])('refuses a disabled forbidden control through %s, before the enabled check', (_, press) => {
    const page = loadSnapshot('workspace-selector');
    const events = recordEvents('pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click');
    const create = found(page.find(CREATE));
    expect(page.read(create).enabled).toBe(false);

    expect(() => {
      press(page, create);
    }).toThrow(ForbiddenControlError);
    expect(events).toEqual([]);
    expect(page.refusal()).toMatchObject({
      control: 'the Create button',
      reason: 'it is the Create button (Songs and Sounds)',
    });
  });

  it('still stops on a disabled control that is allowed, without a refusal', () => {
    const page = loadSnapshot('create-songs-simple');
    const events = recordEvents('click');
    const voice = found(
      page.find({ role: 'button', name: 'Add Voice', description: 'the Add Voice button' }),
    );
    document.querySelector('[aria-label="Add Voice"]')?.setAttribute('disabled', '');

    expect(() => {
      page.click(voice);
    }).toThrow('the Add Voice button to be enabled');
    expect(events).toEqual([]);
    expect(page.refusal()).toBeNull();
  });

  it('gives each run its own handle: a refusal in one run does not stop the page', () => {
    const page = loadSnapshot('create-songs-simple');
    const run = page.withSignal(new AbortController().signal);
    const create = found(run.find(CREATE));

    expect(() => {
      run.click(create);
    }).toThrow(ForbiddenControlError);
    expect(run.refusal()).not.toBeNull();
    expect(page.refusal()).toBeNull();
  });
});

describe('scrollToEnd, scrollListToEnd, and go (the library reader, #134, #422)', () => {
  function byId(id: string): HTMLElement {
    const element = document.getElementById(id);
    if (element === null) {
      throw new Error(`No element #${id}`);
    }
    return element;
  }

  /** Gives an element a scrolling box jsdom does not lay out. */
  function scrolling(element: HTMLElement, height: number, overflow = 'auto'): HTMLElement {
    Object.defineProperty(element, 'scrollHeight', { value: height, configurable: true });
    Object.defineProperty(element, 'clientHeight', { value: 100, configurable: true });
    element.style.overflowY = overflow;
    return element;
  }

  it('scrolls every region that scrolls its own content to its end, pressing nothing', () => {
    document.body.innerHTML = `
      <div id="list"><div role="rowgroup"></div></div>
      <div id="short"></div>
      <div id="clipped"></div>
      <div ${PANEL_HOST_ATTRIBUTE}><div id="panel-list"></div></div>`;
    const list = scrolling(byId('list'), 5000);
    const short = scrolling(byId('short'), 50);
    const clipped = scrolling(byId('clipped'), 5000, 'hidden');
    const panelList = scrolling(byId('panel-list'), 5000);
    const events = recordEvents('click', 'pointerdown', 'keydown', 'mousedown');

    new Page(document).scrollToEnd();

    expect(list.scrollTop).toBe(5000);
    expect(short.scrollTop).toBe(0);
    expect(clipped.scrollTop).toBe(0);
    expect(panelList.scrollTop).toBe(0);
    expect(events).toEqual([]);
  });

  it("scrolls one list's own pane to its end, and no other pane (#422)", () => {
    document.body.innerHTML = `
      <div id="page">
        <div id="form"><button aria-label="Create song">Create</button></div>
        <div id="pane"><div id="inner"><div role="rowgroup"></div></div></div>
      </div>`;
    const page = scrolling(byId('page'), 5000);
    const form = scrolling(byId('form'), 5000);
    const pane = scrolling(byId('pane'), 5000);
    const inner = scrolling(byId('inner'), 50);
    const events = recordEvents('click', 'pointerdown', 'keydown', 'mousedown');
    const handle = new Page(document);
    const rows = handle.find({ role: 'rowgroup', description: 'rows' });
    if (rows.kind !== 'found') {
      throw new Error('no rows');
    }

    handle.scrollListToEnd(rows.found);

    expect(pane.scrollTop).toBe(5000);
    expect(inner.scrollTop).toBe(0);
    expect(form.scrollTop).toBe(0);
    expect(page.scrollTop).toBe(0);
    expect(events).toEqual([]);
  });

  it('leaves a list in nothing that scrolls as it is, and scrolls nothing once its run has stopped', () => {
    document.body.innerHTML = '<div id="form"></div><div role="rowgroup"></div>';
    const form = scrolling(byId('form'), 5000);
    const controller = new AbortController();
    const handle = new Page(document);
    const rows = handle.find({ role: 'rowgroup', description: 'rows' });
    if (rows.kind !== 'found') {
      throw new Error('no rows');
    }

    handle.scrollListToEnd(rows.found);
    expect(form.scrollTop).toBe(0);

    const stopped = handle.withSignal(controller.signal);
    controller.abort();
    expect(() => {
      stopped.scrollListToEnd(rows.found);
    }).toThrow(StoppedError);
  });

  it('loads a Suno page by address, and nothing else', () => {
    const visited: string[] = [];
    const page = new Page(document, { navigate: (address) => visited.push(address) });

    page.go(new URL('https://suno.com/me/trash'));
    expect(() => {
      page.go(new URL('https://example.com/me'));
    }).toThrow(PrimitiveError);
    expect(() => {
      page.go(new URL('http://suno.com/me'));
    }).toThrow('an address on suno.com');

    expect(visited).toEqual(['https://suno.com/me/trash']);
  });

  it('neither scrolls nor navigates once its run has stopped', () => {
    const visited: string[] = [];
    const controller = new AbortController();
    const page = new Page(document, { navigate: (address) => visited.push(address) }).withSignal(
      controller.signal,
    );
    controller.abort();

    expect(() => {
      page.scrollToEnd();
    }).toThrow(StoppedError);
    expect(() => {
      page.go(new URL('https://suno.com/me'));
    }).toThrow(StoppedError);
    expect(visited).toEqual([]);
  });

  it('finds lists by role: a list element and an ARIA rowgroup', () => {
    document.body.innerHTML = '<ul aria-label="8 songs"><li>a</li></ul><div role="rowgroup"></div>';
    const page = new Page(document);

    expect(page.find({ role: 'list', name: /^\d+ songs$/, description: 'songs' }).kind).toBe(
      'found',
    );
    expect(page.find({ role: 'rowgroup', description: 'rows' }).kind).toBe('found');
  });
});

describe('regions and labels (#146)', () => {
  const MORE_OPTIONS: Target = {
    role: 'button',
    name: /^More Options\b/,
    description: 'the More Options section header',
  };

  it('finds an unnamed control beside its label, where the name alone is ambiguous', () => {
    const page = loadSnapshot(ADVANCED);
    const on: Target = {
      role: 'button',
      name: 'On',
      within: {
        around: {
          text: 'Max Mode',
          within: { around: MORE_OPTIONS, levels: 2, description: 'More Options' },
          description: 'the Max Mode label',
        },
        levels: 2,
        description: 'the Max Mode switch',
      },
      description: 'the Max Mode On button',
    };

    expect(page.find({ role: 'button', name: 'On', description: 'On' })).toMatchObject({
      kind: 'ambiguous',
    });
    expect(page.read(found(page.find(on))).selected).toBe(true);
  });

  it('reaches no further out than its levels, so a control that has gone is not found elsewhere', () => {
    const page = loadSnapshot(ADVANCED);
    const variety: Target = {
      role: 'slider',
      name: 'Variety',
      within: { around: MORE_OPTIONS, levels: 2, description: 'More Options' },
      description: 'the Variety slider in More Options',
    };
    expect(page.read(found(page.find(variety))).value).toBe('2');

    document.querySelector('[role="slider"][aria-label="Variety"]')?.remove();

    // The Speech form's Variety slider is further out; it is not taken in its place.
    expect(page.find(variety)).toMatchObject({ kind: 'not_found' });
    expect(
      page.find({ ...variety, within: { ...variety.within, levels: 10 } as Target['within'] }),
    ).toMatchObject({ kind: 'found' });
  });

  it('finds a menu button by the popup it opens, and reads whether it is open', () => {
    const page = loadSnapshot(ADVANCED);
    const model = found(
      page.find({
        role: 'button',
        popup: 'menu',
        within: {
          around: { role: 'tablist', name: 'Create form mode', description: 'the mode tabs' },
          levels: 3,
          description: 'the top of the form',
        },
        description: 'the model button',
      }),
    );

    expect(page.read(model)).toMatchObject({ text: 'v6-mini', expanded: false });
    expect(page.read(found(page.find(MORE_OPTIONS))).expanded).toBe(true);
  });
});

describe('editable text (#146: the Lexical lyrics editor)', () => {
  const EDITOR: Target = {
    role: 'textbox',
    name: 'Lyrics editor',
    description: 'the Lyrics editor',
  };

  it('reads an editable region as its lines: one per paragraph, an empty paragraph as an empty line', () => {
    document.body.innerHTML =
      '<div contenteditable="true" aria-label="Lyrics editor"><p><span>one</span></p><p><br></p><p>two<br>three</p></div>';
    const page = new Page(document);

    expect(page.read(found(page.find(EDITOR))).value).toBe('one\n\ntwo\nthree');
  });

  it('pastes the whole text after the select-all shortcut, keeping every line, blank line, and indent (TS-007-8)', async () => {
    const page = loadSnapshot(ADVANCED);
    const editor = found(page.find(EDITOR));
    const element = document.querySelector<HTMLElement>('[contenteditable="true"]');
    if (element === null) {
      throw new Error('the snapshot has no editable region');
    }
    // Stand-ins for the browser's clipboard event and for Lexical, which takes paste and Backspace
    // only once it has a selection: a task after focus, as `selectionchange` gives it.
    class StandInData {
      readonly stored = new Map<string, string>();
      setData(type: string, value: string) {
        this.stored.set(type, value);
      }
    }
    class StandInClipboardEvent extends Event {
      readonly clipboardData: StandInData;
      constructor(type: string, init: EventInit & { clipboardData: StandInData }) {
        super(type, init);
        this.clipboardData = init.clipboardData;
      }
    }
    Object.assign(window, { DataTransfer: StandInData, ClipboardEvent: StandInClipboardEvent });
    let selected = false;
    element.addEventListener('focus', () => {
      selected = false;
      setTimeout(() => {
        selected = true;
      }, 0);
    });
    const pastes: (string | undefined)[] = [];
    const keys: string[] = [];
    element.addEventListener('paste', (event) => {
      event.preventDefault();
      if (selected) {
        pastes.push(
          (event as unknown as StandInClipboardEvent).clipboardData.stored.get('text/plain'),
        );
      }
    });
    element.addEventListener('keydown', (event) => {
      keys.push(`${event.metaKey || event.ctrlKey ? 'mod+' : ''}${event.key}`);
      if (event.key === 'Backspace') {
        event.preventDefault();
      }
    });
    const commands: string[] = [];
    Object.defineProperty(document, 'execCommand', {
      configurable: true,
      value: (command: string) => commands.push(command),
    });
    try {
      element.blur();
      await page.typeText(editor, '[Verse 1]\r\nFirst line\n  indented\n\n[Chorus]');
      element.blur();
      await page.typeText(editor, '');
    } finally {
      Reflect.deleteProperty(window, 'DataTransfer');
      Reflect.deleteProperty(window, 'ClipboardEvent');
      Reflect.deleteProperty(document, 'execCommand');
    }

    expect(pastes).toEqual(['[Verse 1]\nFirst line\n  indented\n\n[Chorus]']);
    expect(keys).toEqual(['mod+a', 'mod+a', 'Backspace']);
    expect(keys).not.toContain('Enter');
    expect(commands).toEqual([]);
  });

  it('types line by line through the browser’s editing commands where nothing takes the paste', async () => {
    const page = loadSnapshot(ADVANCED);
    const calls: string[] = [];
    Object.defineProperty(document, 'execCommand', {
      configurable: true,
      value: (command: string, _ui: boolean, value?: string) => {
        calls.push(`${command}${value === undefined ? '' : ` ${value}`}`);
        if (calls.length === 1) {
          expect(document.getSelection()?.isCollapsed).toBe(false);
        }
        return true;
      },
    });
    const keys = recordEvents('keydown');
    try {
      await page.typeText(found(page.find(EDITOR)), 'one\r\n\r\ntwo');
      await page.typeText(found(page.find(EDITOR)), '');
    } finally {
      Reflect.deleteProperty(document, 'execCommand');
    }

    expect(calls).toEqual([
      'insertText one',
      'insertParagraph',
      'insertParagraph',
      'insertText two',
      'delete',
    ]);
    expect(keys).toEqual(['keydown', 'keydown', 'keydown']);
  });

  it('refuses where it cannot type: not an editable region, no editing commands, or after the run stopped', async () => {
    const page = loadSnapshot(ADVANCED);
    const editor = found(page.find(EDITOR));

    await expect(page.typeText(found(page.find(WEIRDNESS)), 'x')).rejects.toThrow(
      'the Weirdness slider to be an editable text region',
    );
    await expect(page.typeText(editor, 'x')).rejects.toThrow(
      'a browser that can type into the Lyrics editor',
    );
    expect(() => {
      page.set(editor, 'x');
    }).toThrow('the Lyrics editor to take a typed value');
    const controller = new AbortController();
    controller.abort();
    await expect(page.withSignal(controller.signal).typeText(editor, 'x')).rejects.toThrow(
      StoppedError,
    );
  });
});

describe('sliders that move in steps (#146: Duration by 5 seconds)', () => {
  function durationPage() {
    document.body.innerHTML =
      '<div role="slider" aria-label="Duration" aria-valuenow="30" aria-valuemin="10" aria-valuemax="360"></div>';
    const element = document.querySelector('[role="slider"]');
    element?.addEventListener('keydown', (event) => {
      const now = Number(element.getAttribute('aria-valuenow'));
      const key = (event as KeyboardEvent).key;
      element.setAttribute('aria-valuenow', String(key === 'ArrowRight' ? now + 5 : now - 5));
    });
    const page = new Page(document, { clock: fakeClock() });
    return {
      page,
      slider: found(page.find({ role: 'slider', name: 'Duration', description: 'Duration' })),
    };
  }

  it('ends on the nearer step when the value lies between two', async () => {
    const { page, slider } = durationPage();

    await page.slide(slider, 33);
    expect(page.read(slider).value).toBe('35');
    await page.slide(slider, 47);
    expect(page.read(slider).value).toBe('45');
  });
});

describe('sliders that apply a key a task later (#427, TS-007-9)', () => {
  /**
   * A clock whose sleep lets the page's own tasks run (a timer of 0) and moves its time by what
   * was asked, so a test is deterministic and quick.
   */
  function taskClock(): Clock & { slept: number[] } {
    let now = 0;
    const slept: number[] = [];
    return {
      slept,
      now: () => now,
      sleep: (ms) =>
        new Promise((resolve) => {
          slept.push(ms);
          now += ms;
          setTimeout(resolve, 0);
        }),
    };
  }

  /**
   * Suno's Style Influence slider as seen live: a key is applied a task after it, from the value
   * the slider had when the key came, so keys sent back to back count as one (50 to 51 for five).
   */
  function deferredSlider(start: number, step = 1) {
    document.body.innerHTML = `<div role="slider" aria-label="Style Influence" aria-valuenow="${String(start)}" aria-valuemin="0" aria-valuemax="100"></div>`;
    const element = document.querySelector('[role="slider"]');
    const keys: string[] = [];
    element?.addEventListener('keydown', (event) => {
      const key = (event as KeyboardEvent).key;
      keys.push(key);
      const from = Number(element.getAttribute('aria-valuenow'));
      setTimeout(() => {
        const next = key === 'ArrowRight' ? Math.min(100, from + step) : Math.max(0, from - step);
        element.setAttribute('aria-valuenow', String(next));
      }, 0);
    });
    return { element, keys };
  }

  const STYLE_INFLUENCE: Target = {
    role: 'slider',
    name: 'Style Influence',
    description: 'the Style Influence slider',
  };

  it('counts keys sent back to back once, as seen live', async () => {
    const { element } = deferredSlider(50);
    for (let key = 0; key < 35; key += 1) {
      // Test-only: the keys the old stepping sent, one straight after another.
      element?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    }
    await new Promise((resolve) => setTimeout(resolve, 0));

    expect(element?.getAttribute('aria-valuenow')).toBe('51');
  });

  it('reaches 85 from 50, and 12 back down, a task at a time', async () => {
    const { keys } = deferredSlider(50);
    const page = new Page(document, { clock: taskClock() });
    const slider = found(page.find(STYLE_INFLUENCE));

    await page.slide(slider, 85);
    expect(page.read(slider).value).toBe('85');
    expect(keys).toHaveLength(35);
    expect(new Set(keys)).toEqual(new Set(['ArrowRight']));

    await page.slide(slider, 12);
    expect(page.read(slider).value).toBe('12');
    expect(keys).toHaveLength(35 + 73);
  });

  it('keeps stepping a slider that takes longer than a task, and stops at its end', async () => {
    document.body.innerHTML =
      '<div role="slider" aria-label="Style Influence" aria-valuenow="98" aria-valuemin="0" aria-valuemax="100"></div>';
    const element = document.querySelector('[role="slider"]');
    element?.addEventListener('keydown', () => {
      const from = Number(element.getAttribute('aria-valuenow'));
      // Applied 300 ms (of the clock's time) later, as one key was seen live.
      const due = clock.now() + 300;
      const apply = () => {
        if (clock.now() >= due) {
          element.setAttribute('aria-valuenow', String(Math.min(100, from + 1)));
        } else {
          setTimeout(apply, 0);
        }
      };
      setTimeout(apply, 0);
    });
    const clock = taskClock();
    const page = new Page(document, { clock });
    const slider = found(page.find(STYLE_INFLUENCE));

    await page.slide(slider, 100);
    expect(page.read(slider).value).toBe('100');

    // At its end a key changes nothing: the stepping stops after one wait, not a thousand keys.
    const before = clock.now();
    await page.slide(slider, 100);
    expect(clock.now()).toBe(before);
  });

  it('ends a stepped slider on the nearer step when keys apply a task later', async () => {
    deferredSlider(30, 5);
    const page = new Page(document, { clock: taskClock() });
    const slider = found(page.find(STYLE_INFLUENCE));

    await page.slide(slider, 47);

    expect(page.read(slider).value).toBe('45');
  });

  it('stops when the run stops between keys', async () => {
    deferredSlider(50);
    const controller = new AbortController();
    const base = taskClock();
    // The run stops while the first key is waited for.
    const clock: Clock = {
      now: () => base.now(),
      sleep: async (ms) => {
        await base.sleep(ms);
        controller.abort();
      },
    };
    const page = new Page(document, { clock }).withSignal(controller.signal);
    const slider = found(page.find(STYLE_INFLUENCE));

    await expect(page.slide(slider, 85)).rejects.toBeInstanceOf(StoppedError);
    expect(page.read(slider).value).toBe('51');
  });
});

describe('image addresses (#148: the source’s thumbnail)', () => {
  it('reads the one image a control shows, or an image itself, and nothing when there are several', () => {
    const page = loadSnapshot('create-source-advanced');
    const player = found(page.find({ role: 'button', name: 'Play audio', description: 'player' }));

    expect(page.imageAddress(player)).toBe(
      'https://cdn2.suno.ai/image_00000000-0000-4000-8000-000000000104.jpeg',
    );
    const image = found(
      page.find({ role: 'img', name: /^Cover art for /, description: 'the cover art' }),
    );
    expect(page.imageAddress(image)).toBe(page.imageAddress(player));

    document.body.innerHTML =
      '<ul aria-label="Two"><li><img src="https://a.example/1.jpeg" alt=""></li><li><img src="https://a.example/2.jpeg" alt=""></li></ul><ul aria-label="None"><li>No image</li></ul>';
    const two = found(page.find({ role: 'list', name: 'Two', description: 'two' }));
    const none = found(page.find({ role: 'list', name: 'None', description: 'none' }));
    expect(page.imageAddress(two)).toBeNull();
    expect(page.imageAddress(none)).toBeNull();
  });
});
