import type { Listed } from '../adapter/observations.ts';
import type { ImageProgress, SyncCounts, SyncScope } from '../messages.ts';

/**
 * Sync to n8Tracks, in the panel on Suno (#134): the user chooses what to read (the whole library,
 * one or more workspaces, or one or more playlists), reads a summary, and confirms; nothing starts
 * before that. While it reads, it shows counts and Cancel; when it stops, it names the step that
 * failed and offers Try again. Plain DOM in the panel's shadow root, every control labelled and
 * reachable by keyboard.
 */

export type SyncViewState =
  | { kind: 'choose' }
  | { kind: 'summary'; scope: SyncScope; replacesReady: boolean }
  | { kind: 'reading'; step: string; counts: SyncCounts }
  | { kind: 'finished'; counts: SyncCounts; images?: ImageProgress | null }
  | { kind: 'stopped'; report: string }
  | { kind: 'cancelled' };

export interface SyncViewOptions {
  /** The workspaces and playlists Suno has listed on this page, to choose from. */
  choices(): { workspaces: Listed[]; playlists: Listed[] };
  /** "Next": the summary needs to know whether an export waiting for review will be replaced. */
  preview(scope: SyncScope): void;
  /** "Start sync", after the summary. */
  start(scope: SyncScope): void;
  cancel(): void;
}

type ScopeKind = SyncScope['kind'];

const SCOPE_LABELS: Record<ScopeKind, string> = {
  library: 'Whole library',
  workspaces: 'Workspaces',
  playlists: 'Playlists',
};

function plural(count: number, one: string, many: string): string {
  return `${String(count)} ${count === 1 ? one : many}`;
}

/** The counts, as the panel says them. */
export function countsText(counts: SyncCounts): string {
  return [
    plural(counts.workspaces, 'workspace', 'workspaces'),
    plural(counts.clips, 'clip', 'clips'),
    plural(counts.trashed, 'clip in the Trash', 'clips in the Trash'),
    plural(counts.playlists, 'playlist', 'playlists'),
  ].join(', ');
}

/** What the summary says will be read. */
export function summaryText(scope: SyncScope): string {
  switch (scope.kind) {
    case 'library':
      return 'Reads your whole Suno library, in every workspace (the number of clips is not known until it has been read), plus the Trash list and the workspace list.';
    case 'workspaces':
      return `Reads the clips of ${plural(scope.ids.length, 'workspace', 'workspaces')} from your Suno library, plus the Trash list and the workspace list.`;
    case 'playlists':
      return `Reads ${plural(scope.playlists.length, 'playlist', 'playlists')} with their songs, plus the Trash list and the workspace list.`;
  }
}

/**
 * What the panel says about a sync's cover images (#152), or null before there is anything to
 * say. The user may start the review while they are sent; an image that could not be read leaves
 * its Generation without artwork, and never fails the sync.
 */
export function imagesText(images: ImageProgress | null | undefined): string | null {
  if (images === null || images === undefined) {
    return null;
  }
  const sent = `${String(images.sent)} of ${String(images.total)} sent`;
  const failed =
    images.failed === 0
      ? ''
      : ` ${plural(images.failed, 'image', 'images')} could not be read or sent; ${images.failed === 1 ? 'that Generation is' : 'those Generations are'} imported without artwork.`;
  switch (images.state) {
    case 'collecting':
      return null;
    case 'skipped':
      return 'Cover images are not brought along: Suno does not let the extension read them without signing in, so the Generations are imported without artwork.';
    case 'waiting':
      return `Cover images: waiting for n8Tracks to get the export ready (${plural(images.total, 'image', 'images')} to send).`;
    case 'sending':
      return `Cover images: ${sent} so far. You can start the review meanwhile.${failed}`;
    case 'finished':
      return images.total === 0 ? 'No cover images to send.' : `Cover images: ${sent}.${failed}`;
    case 'stopped':
      return `Cover images stopped: ${sent}; the export is no longer waiting for them, or the extension was disconnected.${failed}`;
  }
}

function nameOrUnnamed(name: string): string {
  return name.trim() === '' ? '(unnamed)' : name;
}

export class SyncView {
  readonly element: HTMLElement;
  private readonly page: Document;
  private readonly options: SyncViewOptions;
  private readonly body: HTMLElement;
  private readonly unavailable: HTMLElement;
  private available = false;
  private chosenKind: ScopeKind = 'library';
  private chosenIds = new Set<string>();
  private state: SyncViewState = { kind: 'choose' };
  /** The finished state's line about cover images, updated in place so focus stays put. */
  private imagesLine: HTMLElement | null = null;

  constructor(page: Document, options: SyncViewOptions) {
    this.page = page;
    this.options = options;
    this.element = this.make('section', { class: 'sync', 'aria-labelledby': 'n8-sync-title' });
    const heading = this.make('h3', { id: 'n8-sync-title' }, 'Sync to n8Tracks');
    this.unavailable = this.make('p', { class: 'detail' });
    this.unavailable.hidden = true;
    this.body = this.make('div');
    this.element.append(heading, this.unavailable, this.body);
    this.render();
  }

  /** Whether a sync may start: connected, with `suno.sync`. A version mismatch still allows it. */
  setAvailable(available: boolean, reason: string | null): void {
    this.available = available;
    this.unavailable.textContent = available ? '' : (reason ?? 'Connect the extension to sync.');
    this.unavailable.hidden = available;
    if (this.state.kind === 'choose' || this.state.kind === 'summary') {
      this.render();
    }
  }

  get current(): SyncViewState {
    return this.state;
  }

  show(state: SyncViewState): void {
    const moved = state.kind !== this.state.kind;
    this.state = state;
    this.render();
    if (moved) {
      this.focusFirst();
    }
  }

  /** The cover images of the finished sync changed (#152): only their line is written again. */
  setImages(images: ImageProgress | null): void {
    if (this.state.kind !== 'finished') {
      return;
    }
    this.state = { ...this.state, images };
    this.writeImages(images);
  }

  private writeImages(images: ImageProgress | null | undefined): void {
    const text = imagesText(images);
    if (this.imagesLine !== null) {
      this.imagesLine.textContent = text ?? '';
      this.imagesLine.hidden = text === null;
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

  private button(text: string, onPress: () => void, disabled = false): HTMLButtonElement {
    const button = this.make('button', { type: 'button' }, text);
    button.disabled = disabled;
    button.addEventListener('click', onPress);
    return button;
  }

  private firstControl: HTMLElement | null = null;

  private focusFirst(): void {
    if (this.firstControl?.isConnected === true) {
      this.firstControl.focus();
    }
  }

  private scope(): SyncScope | null {
    const { workspaces, playlists } = this.options.choices();
    switch (this.chosenKind) {
      case 'library':
        return { kind: 'library' };
      case 'workspaces': {
        const ids = workspaces.filter((item) => this.chosenIds.has(item.id)).map((item) => item.id);
        return ids.length === 0 ? null : { kind: 'workspaces', ids };
      }
      case 'playlists': {
        const chosen = playlists.filter((item) => this.chosenIds.has(item.id));
        return chosen.length === 0 ? null : { kind: 'playlists', playlists: chosen };
      }
    }
  }

  private render(): void {
    this.firstControl = null;
    this.imagesLine = null;
    this.body.replaceChildren();
    switch (this.state.kind) {
      case 'choose':
        this.renderChoice();
        return;
      case 'summary':
        this.renderSummary(this.state.scope, this.state.replacesReady);
        return;
      case 'reading': {
        const status = this.make(
          'p',
          { role: 'status', class: 'sync-progress' },
          `Reading: ${this.state.step}. Read so far: ${countsText(this.state.counts)}.`,
        );
        const note = this.make(
          'p',
          { class: 'detail' },
          'Keep this Suno tab open and visible until the sync finishes.',
        );
        const cancel = this.button('Cancel sync', () => {
          this.options.cancel();
        });
        this.firstControl = cancel;
        this.body.append(status, note, cancel);
        return;
      }
      case 'finished': {
        const done = this.make(
          'p',
          { role: 'status', class: 'sync-done' },
          `Finished: read ${countsText(this.state.counts)}. The review is open in n8Tracks.`,
        );
        // A status region of its own, so its count updates are announced without moving focus.
        const images = this.make('p', { role: 'status', class: 'sync-images' });
        this.imagesLine = images;
        this.writeImages(this.state.images);
        const again = this.button('Sync again', () => {
          this.show({ kind: 'choose' });
        });
        this.firstControl = again;
        this.body.append(done, images, again);
        return;
      }
      case 'stopped': {
        const stopped = this.make(
          'p',
          { role: 'alert', class: 'sync-stopped' },
          `Sync stopped: ${this.state.report}. Nothing was sent for review.`,
        );
        const again = this.button('Try again', () => {
          this.show({ kind: 'choose' });
        });
        again.setAttribute('aria-label', 'Try again: Sync to n8Tracks');
        this.firstControl = again;
        this.body.append(stopped, again);
        return;
      }
      case 'cancelled': {
        const cancelled = this.make(
          'p',
          { role: 'status', class: 'sync-cancelled' },
          'Sync cancelled. Nothing more was read, and nothing was sent for review.',
        );
        const again = this.button('Start again', () => {
          this.show({ kind: 'choose' });
        });
        this.firstControl = again;
        this.body.append(cancelled, again);
        return;
      }
    }
  }

  private renderChoice(): void {
    const scopes = this.make('fieldset', { class: 'sync-scope' });
    scopes.append(this.make('legend', {}, 'What to read'));
    for (const kind of ['library', 'workspaces', 'playlists'] as const) {
      const label = this.make('label');
      const radio = this.make('input', { type: 'radio', name: 'n8-sync-scope', value: kind });
      radio.checked = this.chosenKind === kind;
      radio.disabled = !this.available;
      radio.addEventListener('change', () => {
        if (radio.checked) {
          this.chosenKind = kind;
          this.chosenIds = new Set();
          this.render();
          this.focusFirst();
        }
      });
      if (this.firstControl === null || radio.checked) {
        this.firstControl = radio;
      }
      label.append(radio, ` ${SCOPE_LABELS[kind]}`);
      scopes.append(label);
    }
    this.body.append(scopes);

    if (this.chosenKind !== 'library') {
      this.body.append(this.renderList(this.chosenKind));
    }

    const problem = this.make('p', { class: 'warning', id: 'n8-sync-problem' });
    problem.hidden = true;
    const next = this.button(
      'Next',
      () => {
        const scope = this.scope();
        if (scope === null) {
          problem.textContent =
            this.chosenKind === 'workspaces'
              ? 'Choose at least one workspace.'
              : 'Choose at least one playlist.';
          problem.hidden = false;
          return;
        }
        this.options.preview(scope);
      },
      !this.available,
    );
    next.setAttribute('aria-describedby', 'n8-sync-problem');
    this.body.append(problem, next);
  }

  private renderList(kind: 'workspaces' | 'playlists'): HTMLElement {
    const listed = this.options.choices()[kind];
    const group = this.make('fieldset', { class: 'sync-list' });
    group.append(
      this.make('legend', {}, kind === 'workspaces' ? 'Workspaces to read' : 'Playlists to read'),
    );
    if (listed.length === 0) {
      group.append(
        this.make(
          'p',
          { class: 'detail' },
          kind === 'workspaces'
            ? 'No workspaces yet: open Library › Workspaces on Suno, then choose again.'
            : 'No playlists yet: open Library › Playlists on Suno, then choose again.',
        ),
      );
      return group;
    }
    group.append(
      this.make(
        'p',
        { class: 'detail' },
        `These are the ${kind} Suno has shown on this page; scroll its list to see more.`,
      ),
    );
    for (const item of listed) {
      const label = this.make('label');
      const box = this.make('input', { type: 'checkbox', value: item.id });
      box.checked = this.chosenIds.has(item.id);
      box.disabled = !this.available;
      box.addEventListener('change', () => {
        if (box.checked) {
          this.chosenIds.add(item.id);
        } else {
          this.chosenIds.delete(item.id);
        }
      });
      label.append(box, ` ${nameOrUnnamed(item.name)}`);
      group.append(label);
    }
    return group;
  }

  private renderSummary(scope: SyncScope, replacesReady: boolean): void {
    const what = this.make('p', { class: 'sync-summary' }, summaryText(scope));
    const keep = this.make(
      'p',
      { class: 'detail' },
      'The extension operates this Suno page while it reads, and opens the review in n8Tracks when it finishes. Keep this tab open and visible until then.',
    );
    this.body.append(what, keep);
    if (replacesReady) {
      this.body.append(
        this.make(
          'p',
          { class: 'warning' },
          'An export is waiting for review in n8Tracks; this sync replaces it when it finishes.',
        ),
      );
    }
    const start = this.button(
      'Start sync',
      () => {
        this.options.start(scope);
      },
      !this.available,
    );
    const back = this.button('Back', () => {
      this.show({ kind: 'choose' });
    });
    this.firstControl = start;
    this.body.append(start, ' ', back);
  }
}
