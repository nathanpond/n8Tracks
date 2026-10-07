import type { EntryResult } from '../adapter/fill.ts';
import type { WorkspaceOption } from '../adapter/workspaces.ts';
import { verificationSummary } from './VerificationSummary.ts';

/**
 * Generate on Suno, in the panel on Suno (#145): what the extension is doing for the request
 * n8Tracks handed it, and, when the Song has no workspace (or its workspace is gone from Suno), the
 * user's choice: create a workspace named after the Song, or use one Suno already has. Nothing is
 * created until the user presses one. Plain DOM in the panel's shadow root, every control labelled
 * and reachable by keyboard. Hidden until a request reaches this tab.
 */

export type GenerateViewState =
  | { kind: 'idle' }
  | { kind: 'working'; step: string }
  | {
      kind: 'choose';
      title: string;
      /** The Song has no workspace, or the one it had is no longer in Suno's list. */
      reason: 'none' | 'unavailable';
      options: readonly WorkspaceOption[];
    }
  | { kind: 'selected'; name: string }
  /** The form is filled (#146): the summary, and the user reviews the form and clicks Create. */
  | { kind: 'verification'; mode: string; results: readonly EntryResult[]; checkedAt: Date }
  | { kind: 'stopped'; message: string };

export interface GenerateViewOptions {
  /** "Create workspace": the user chose to create one named after the Song. */
  create(): void;
  /** "Use": the user chose an existing workspace. */
  pick(option: WorkspaceOption): void;
  /** "Check again" in the verification summary (#146). */
  checkAgain?(): void;
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
      case 'choose':
        this.renderChoice(this.state.title, this.state.reason, this.state.options);
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
        );
        this.firstControl = summary.firstControl;
        this.body.append(summary.element);
        return;
      }
    }
  }

  private renderChoice(
    title: string,
    reason: 'none' | 'unavailable',
    options: readonly WorkspaceOption[],
  ): void {
    const why = this.make(
      'p',
      { class: 'generate-choice-reason', id: 'n8-generate-reason' },
      reason === 'none'
        ? `The Song “${nameOrUnnamed(title)}” has no Suno workspace yet. Choose where its Generations go; the extension uses it for every later Generation of the Song.`
        : `The Song’s workspace was not found in Suno’s list, so n8Tracks marked it Unavailable. Choose a replacement for the Song “${nameOrUnnamed(title)}”.`,
    );
    const create = this.button(`Create a workspace named “${nameOrUnnamed(title)}”`, () => {
      this.options.create();
    });
    create.setAttribute('aria-describedby', 'n8-generate-reason');
    this.firstControl = create;
    this.body.append(why, create);

    const existing = this.make('section', {
      class: 'generate-existing',
      'aria-labelledby': 'n8-generate-existing',
    });
    existing.append(
      this.make('h4', { id: 'n8-generate-existing' }, 'Or use a workspace Suno already has'),
    );
    if (options.length === 0) {
      existing.append(this.make('p', { class: 'detail' }, 'Suno listed no other workspace.'));
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
}
