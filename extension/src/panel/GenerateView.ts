import type { EntryResult } from '../adapter/fill.ts';
import type { WorkspaceOption } from '../adapter/workspaces.ts';
import { verificationSummary } from './VerificationSummary.ts';

/**
 * Generate on Suno, in the panel on Suno (#145): what the extension is doing for the request
 * n8Tracks handed it, and, when the Song has no usable workspace (none, Unavailable, or not shown
 * by its address), the user's choice: create a workspace named after the Song, or use one Suno
 * already has, found among what Suno loaded or by a search typed into Suno's own search box
 * (TS-007-15). Nothing is created until the user presses one. Plain DOM in the panel's shadow root, every control labelled
 * and reachable by keyboard. Hidden until a request reaches this tab.
 */

export type GenerateViewState =
  | { kind: 'idle' }
  | { kind: 'working'; step: string }
  | {
      kind: 'choose';
      title: string;
      /**
       * The Song has no workspace, n8Tracks holds its one Unavailable, or Suno did not show it by
       * its address (`unopened`).
       */
      reason: 'none' | 'unavailable' | 'unopened';
      /** The search the options are narrowed to; blank for none. */
      query?: string;
      /** Suno has a workspace with the Song's title already (#335): the new one has its name too. */
      sameName?: boolean;
      options: readonly WorkspaceOption[];
    }
  | { kind: 'selected'; name: string }
  /**
   * The form is filled (#146): the summary, and the user reviews the form and clicks Create.
   * `form` is the Version's kind (#147: `song`, `speech`, or `sound`); a Song's when absent.
   */
  | {
      kind: 'verification';
      form?: string;
      mode: string;
      results: readonly EntryResult[];
      checkedAt: Date;
    }
  /**
   * The user's Create (#149): what n8Tracks recorded (`recorded`), or why its clips were not recorded.
   * The request goes on waiting for further Creates, and once one is recorded the panel offers Done
   * (#430), unless the request has `ended` (the tab left Create, or the user pressed Done).
   */
  | { kind: 'recorded'; recorded: boolean; message: string; ended?: boolean }
  /**
   * The source on Suno's form is not the Version's (#148): nothing else is filled until the user
   * loads it by hand and presses Continue, which checks it again.
   */
  | { kind: 'source'; message: string }
  | { kind: 'stopped'; message: string };

export interface GenerateViewOptions {
  /** "Create workspace": the user chose to create one named after the Song. */
  create(): void;
  /** "Use": the user chose an existing workspace. */
  pick(option: WorkspaceOption): void;
  /** "Search": the user looks for a workspace by name in Suno's own search box. */
  search?(query: string): void;
  /** "Check again" in the verification summary (#146). */
  checkAgain?(): void;
  /** "Continue" once the user loaded the source by hand (#148). */
  continueSource?(): void;
  /** "Done" once a Create is recorded (#430): the request ends as finished. */
  finish?(): void;
}

function nameOrUnnamed(name: string): string {
  return name.trim() === '' ? '(unnamed)' : name;
}

/** How an existing workspace is described beside its name. */
export function optionDetail(option: WorkspaceOption): string {
  if (option.sameName) {
    return 'Same name as the Song, no Song in it yet';
  }
  return option.songCount === 0
    ? 'No n8Tracks Song in it'
    : `${String(option.songCount)} n8Tracks ${option.songCount === 1 ? 'Song' : 'Songs'} in it`;
}

export class GenerateView {
  readonly element: HTMLElement;
  private readonly page: Document;
  private readonly options: GenerateViewOptions;
  private readonly body: HTMLElement;
  private state: GenerateViewState = { kind: 'idle' };
  private firstControl: HTMLElement | null = null;
  /** The choice's buttons, all disabled once one is pressed. */
  private controls: HTMLButtonElement[] = [];

  constructor(page: Document, options: GenerateViewOptions) {
    this.page = page;
    this.options = options;
    this.element = this.make('section', {
      class: 'generate',
      'aria-labelledby': 'n8-generate-title',
    });
    const heading = this.make('h3', { id: 'n8-generate-title' }, 'Generate on Suno');
    this.body = this.make('div');
    this.element.append(heading, this.body);
    this.render();
  }

  get current(): GenerateViewState {
    return this.state;
  }

  show(state: GenerateViewState): void {
    // A new summary (after Check again) is a move too: focus returns to its Check again button.
    const moved = state.kind !== this.state.kind || state.kind === 'verification';
    this.state = state;
    this.render();
    if (moved && this.firstControl?.isConnected === true) {
      this.firstControl.focus();
    }
  }

  private make<K extends keyof HTMLElementTagNameMap>(
    tag: K,
    attributes: Record<string, string> = {},
    text?: string,
  ): HTMLElementTagNameMap[K] {
    const element = this.page.createElement(tag);
    for (const [name, value] of Object.entries(attributes)) {
      element.setAttribute(name, value);
    }
    if (text !== undefined) {
      element.textContent = text;
    }
    return element;
  }

  private button(text: string, onPress: () => void): HTMLButtonElement {
    const button = this.make('button', { type: 'button' }, text);
    button.addEventListener('click', () => {
      // One choice per offer: every control is disabled as soon as one is pressed.
      for (const control of this.controls) {
        control.disabled = true;
      }
      onPress();
    });
    this.controls.push(button);
    return button;
  }

  private render(): void {
    this.firstControl = null;
    this.controls = [];
    this.body.replaceChildren();
    this.element.hidden = this.state.kind === 'idle';
    switch (this.state.kind) {
      case 'idle':
        return;
      case 'working':
        this.body.append(
          this.make('p', { role: 'status', class: 'generate-progress' }, `${this.state.step}…`),
          this.make(
            'p',
            { class: 'detail' },
            'Keep this Suno tab open; n8Tracks shows the progress on the Version page.',
          ),
        );
        return;
      case 'selected':
        this.body.append(
          this.make(
            'p',
            { role: 'status', class: 'generate-selected' },
            `The Song’s workspace “${nameOrUnnamed(this.state.name)}” is selected on Create.`,
          ),
        );
        return;
      case 'recorded': {
        const open = this.state.recorded && this.state.ended !== true;
        this.body.append(
          this.make(
            'p',
            this.state.recorded
              ? { role: 'status', class: 'generate-recorded', id: 'n8-generate-recorded' }
              : { role: 'alert', class: 'generate-not-recorded' },
            this.state.message,
          ),
        );
        if (this.state.ended === true) {
          return;
        }
        this.body.append(
          this.make(
            'p',
            { class: 'detail' },
            open
              ? 'Each further Create on this form is recorded too; press Done when you have finished. The extension never clicks Create.'
              : 'Each further Create on this form is recorded too. The extension never clicks Create.',
          ),
        );
        if (open && this.options.finish !== undefined) {
          const done = this.button('Done', () => {
            this.options.finish?.();
          });
          done.setAttribute('aria-describedby', 'n8-generate-recorded');
          this.body.append(done);
        }
        return;
      }
      case 'stopped':
        this.body.append(
          this.make(
            'p',
            { role: 'alert', class: 'generate-stopped' },
            `Generate on Suno stopped: ${this.state.message}`,
          ),
          this.make(
            'p',
            { class: 'detail' },
            'Start again from the Version page in n8Tracks when you are ready.',
          ),
        );
        return;
      case 'source': {
        const go = this.button('Continue', () => {
          this.options.continueSource?.();
        });
        go.setAttribute('aria-describedby', 'n8-generate-source');
        this.firstControl = go;
        this.body.append(
          this.make(
            'p',
            { role: 'alert', class: 'generate-source', id: 'n8-generate-source' },
            this.state.message,
          ),
          this.make(
            'p',
            { class: 'detail' },
            'Load the source on Suno’s form by hand, then press Continue: the extension checks it again before filling anything else.',
          ),
          go,
        );
        return;
      }
      case 'choose':
        this.renderChoice(this.state);
        return;
      case 'verification': {
        const summary = verificationSummary(
          this.page,
          this.state.mode,
          this.state.results,
          this.state.checkedAt,
          {
            checkAgain: () => {
              this.options.checkAgain?.();
            },
          },
          this.state.form,
        );
        this.firstControl = summary.firstControl;
        this.body.append(summary.element);
        return;
      }
    }
  }

  private renderChoice(state: Extract<GenerateViewState, { kind: 'choose' }>): void {
    const { title, reason, options } = state;
    const query = state.query ?? '';
    const why = this.make(
      'p',
      { class: 'generate-choice-reason', id: 'n8-generate-reason' },
      reason === 'none'
        ? `The Song “${nameOrUnnamed(title)}” has no Suno workspace yet. Choose where its Generations go; the extension uses it for every later Generation of the Song.`
        : reason === 'unavailable'
          ? `The Song’s workspace is Unavailable in n8Tracks (Suno no longer listed it). Choose a replacement for the Song “${nameOrUnnamed(title)}”.`
          : `Suno did not open the Song’s workspace by its address: it may have been deleted in Suno. Choose a replacement for the Song “${nameOrUnnamed(title)}”.`,
    );
    const create = this.button(`Create a workspace named “${nameOrUnnamed(title)}”`, () => {
      this.options.create();
    });
    create.setAttribute('aria-describedby', 'n8-generate-reason');
    this.firstControl = create;
    this.body.append(why, create);
    if (state.sameName === true) {
      this.body.append(
        this.make(
          'p',
          { class: 'detail' },
          'Suno already has a workspace with this name. A new one gets the same name; the extension tells them apart by their IDs.',
        ),
      );
    }

    const existing = this.make('section', {
      class: 'generate-existing',
      'aria-labelledby': 'n8-generate-existing',
    });
    existing.append(
      this.make('h4', { id: 'n8-generate-existing' }, 'Or use a workspace Suno already has'),
    );
    if (this.options.search !== undefined) {
      existing.append(this.searchForm(query));
    }
    if (options.length === 0) {
      existing.append(
        this.make(
          'p',
          { class: 'detail' },
          query.trim() === ''
            ? 'Suno showed no other workspace. Search for one by name.'
            : `Suno found no workspace named like “${query}”.`,
        ),
      );
    } else {
      const list = this.make('ul', { class: 'generate-options' });
      options.forEach((option, index) => {
        const item = this.make('li');
        const detailId = `n8-generate-option-${String(index)}`;
        const use = this.button(`Use “${nameOrUnnamed(option.name)}”`, () => {
          this.options.pick(option);
        });
        use.setAttribute('aria-describedby', detailId);
        item.append(
          use,
          ' ',
          this.make('span', { id: detailId, class: 'detail' }, optionDetail(option)),
        );
        list.append(item);
      });
      existing.append(list);
    }
    this.body.append(existing);
  }

  /**
   * The search: a labelled text box and its button. Suno lists only some of a long workspace list
   * (TS-007-15), so a workspace is found by name in Suno's own search box.
   */
  private searchForm(query: string): HTMLElement {
    const search = this.make('div', { class: 'generate-search', role: 'search' });
    const label = this.make('label', { for: 'n8-generate-search' }, 'Find a workspace by name');
    const box = this.make('input', { id: 'n8-generate-search', type: 'search' });
    box.value = query;
    // A plain button, not a form's submit: nothing in the panel submits a form on Suno's page.
    const go = this.button('Search', () => {
      this.options.search?.(box.value);
    });
    search.append(label, ' ', box, ' ', go);
    return search;
  }
}
