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
  type Target,
} from './primitives.ts';

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

  it('moves a slider without an input by arrow keys, never Enter, until it reads the value', () => {
    const page = loadSnapshot(ADVANCED);
    const element = document.querySelector('[aria-label="Weirdness"]');
    const keys: string[] = [];
    element?.addEventListener('keydown', (event) => {
      const key = (event as KeyboardEvent).key;
      keys.push(key);
      const now = Number(element.getAttribute('aria-valuenow'));
      element.setAttribute('aria-valuenow', String(key === 'ArrowRight' ? now + 1 : now - 1));
    });
    const slider = found(page.find(WEIRDNESS));

    page.set(slider, 73);

    expect(page.read(slider).value).toBe('73');
    expect(keys).toEqual(['ArrowRight', 'ArrowRight', 'ArrowRight']);
  });

  it('leaves a slider that ignores keys as it was, for the read-back to report', () => {
    const page = loadSnapshot(ADVANCED);
    const slider = found(page.find(WEIRDNESS));

    page.set(slider, 40);

    expect(page.read(slider).value).toBe('70');
  });

  it('sets a slider through its range input when it has one', () => {
    document.body.innerHTML =
      '<div role="slider" aria-label="Level" aria-valuenow="1"><input type="range" min="0" max="10" value="1" /></div>';
    const page = new Page(document);
    const events = recordEvents('input');
    const slider = found(page.find({ role: 'slider', name: 'Level', description: 'Level' }));

    page.set(slider, 6);

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
    }).toThrow('the create tabs to offer "Videos"');
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

describe('scrollToEnd and go (the library reader, #134)', () => {
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

  it('types line by line through the browser’s editing commands, with everything selected first', () => {
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
      page.typeText(found(page.find(EDITOR)), 'one\r\n\r\ntwo');
      page.typeText(found(page.find(EDITOR)), '');
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
    expect(keys).toEqual([]);
  });

  it('refuses where it cannot type: not an editable region, no editing commands, or after the run stopped', () => {
    const page = loadSnapshot(ADVANCED);
    const editor = found(page.find(EDITOR));

    expect(() => {
      page.typeText(found(page.find(WEIRDNESS)), 'x');
    }).toThrow('the Weirdness slider to be an editable text region');
    expect(() => {
      page.typeText(editor, 'x');
    }).toThrow('a browser that can type into the Lyrics editor');
    expect(() => {
      page.set(editor, 'x');
    }).toThrow('the Lyrics editor to take a typed value');
    const controller = new AbortController();
    controller.abort();
    expect(() => {
      page.withSignal(controller.signal).typeText(editor, 'x');
    }).toThrow(StoppedError);
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
    const page = new Page(document);
    return {
      page,
      slider: found(page.find({ role: 'slider', name: 'Duration', description: 'Duration' })),
    };
  }

  it('ends on the nearer step when the value lies between two', () => {
    const { page, slider } = durationPage();

    page.set(slider, 33);
    expect(page.read(slider).value).toBe('35');
    page.set(slider, 47);
    expect(page.read(slider).value).toBe('45');
  });
});
