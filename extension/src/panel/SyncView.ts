import type { Listed } from '../adapter/observations.ts';
import type { ImageProgress, SyncCounts, SyncScope } from '../messages.ts';

/**
 * Sync to n8Tracks, in the panel on Suno (#134): the user chooses what to read (the whole library,
 * one or more workspaces, or one or more playlists), reads a summary, and confirms; nothing starts
 * before that. While it reads, it shows counts and Cancel; when it stops, it names the step that
 * failed and offers Try again. Plain DOM in the panel's shadow root, every control labelled and
 * reachable by keyboard.
 *
 * The Workspaces choice (#448) offers the workspaces n8Tracks knows, from earlier syncs and Generate
 * runs, with any Suno has shown this page since; "Refresh from Suno" reads Suno's workspace list
 * again (on Library, as a sync's first leg does) and reports it to n8Tracks. The Playlists choice
 * (#449) does the same with the playlists n8Tracks knows and Suno's playlist list.
 */

export type SyncViewState =
  /** `scope`: the choice to show chosen (a Refresh from Suno comes back to Workspaces, #448). */
  | { kind: 'choose'; scope?: ScopeKind }
  | { kind: 'summary'; scope: SyncScope; replacesReady: boolean }
  | { kind: 'reading'; step: string; counts: SyncCounts }
  | {
      kind: 'finished';
      counts: SyncCounts;
      images?: ImageProgress | null;
      /** About the user's voices (#439): why they were not read or sent, or read only in part. */
      note?: string;
      /** About the playlist list and the playlists read in part (#449). */
      notes?: string[];
    }
  | { kind: 'stopped'; report: string }
  | { kind: 'cancelled' };

/**
 * Where the Workspaces choice's list stands (#448): `loading` while n8Tracks is asked for the
 * workspaces it knows; `refreshing` while Suno's list is read; `refreshed` once it was, with how many
 * were read and whether to the list's end; `failed` with why, in plain words.
 */
export type WorkspaceListState =
  | { kind: 'ready' }
  | { kind: 'loading' }
  | { kind: 'refreshing' }
  | { kind: 'refreshed'; count: number; complete: boolean }
  | { kind: 'failed'; message: string };

export interface SyncViewOptions {
  /**
   * What to choose from: the workspaces (#448) and the playlists (#449) n8Tracks knows, each with
   * those Suno has listed on this page.
   */
  choices(): { workspaces: Listed[]; playlists: Listed[] };
  /** The Workspaces choice was opened: ask n8Tracks for the workspaces it knows (#448). */
  wantWorkspaces(): void;
  /** "Refresh from Suno" (#448): read Suno's workspace list again. */
  refresh(): void;
  /** The Playlists choice was opened: ask n8Tracks for the playlists it knows (#449). */
  wantPlaylists?(): void;
  /** The Playlists choice's "Refresh from Suno" (#449): read Suno's playlist list again. */
  refreshPlaylists?(): void;
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
    ...(counts.voices === undefined ? [] : [plural(counts.voices, 'voice', 'voices')]),
  ].join(', ');
}

/** What the summary says will be read. */
export function summaryText(scope: SyncScope): string {
  const lists =
    scope.kind === 'library' || scope.readTrash
      ? 'plus the Trash list, the workspace list, your voices, and your playlist list.'
      : 'plus the workspace list, your voices, and your playlist list (not the Trash).';
  switch (scope.kind) {
    case 'library':
      return `Reads your whole Suno library, in every workspace (the number of clips is not known until it has been read), ${lists}`;
    case 'workspaces':
      return `Reads the clips of ${plural(scope.ids.length, 'workspace', 'workspaces')} from your Suno library, ${lists}`;
    case 'playlists':
      return `Reads which songs ${plural(scope.playlists.length, 'playlist holds', 'playlists hold')} (their songs come from library and workspace syncs), ${lists}`;
  }
}

/** What the Workspaces choice says about its list (#448), or null when there is nothing to say. */
export function workspaceListText(state: WorkspaceListState, known: number): string | null {
  return knownListText(state, known, 'workspace', 'workspaces');
}

/** What the Playlists choice says about its list (#449), or null when there is nothing to say. */
export function playlistListText(state: WorkspaceListState, known: number): string | null {
  return knownListText(state, known, 'playlist', 'playlists');
}

function knownListText(
  state: WorkspaceListState,
  known: number,
  one: string,
  many: string,
): string | null {
  switch (state.kind) {
    case 'loading':
      return `Asking n8Tracks for the ${many} it knows…`;
    case 'refreshing':
      return `Reading your ${one} list on Suno…`;
    case 'refreshed':
      return state.complete
        ? `Read ${plural(state.count, one, many)} from Suno.`
        : `Read ${plural(state.count, one, many)} from Suno; Suno sent no further page, so some may be missing. Scrolling Suno's list by hand loads more.`;
    case 'failed':
      return state.message;
    case 'ready':
      return known === 0
        ? `n8Tracks knows no ${many} yet. Refresh from Suno to read your ${one} list.`
        : null;
  }
}

/** The hint beside "Also check Suno's Trash" (#420). */
export const TRASH_HINT =
  "Songs moved to Suno's Trash are archived in n8Tracks at the next sync that reads the Trash. Reading it takes longer; a whole-library sync always reads it.";

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
  /** "Also check Suno's Trash", for a workspace or playlist sync (#420): off until the user turns it on. */
  private readTrash = false;
  private state: SyncViewState = { kind: 'choose' };
  /** Where the Workspaces choice's list stands (#448). */
  private workspaceList: WorkspaceListState = { kind: 'ready' };
  /** Where the Playlists choice's list stands (#449). */
  private playlistList: WorkspaceListState = { kind: 'ready' };
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
    // Where focus is in the view, so drawing the Workspaces list again keeps it there (#448).
    this.element.addEventListener('focusin', (event) => {
      this.focused = event.target instanceof Node ? event.target : null;
    });
    this.element.addEventListener('focusout', (event) => {
      const next = event.relatedTarget;
      if (!(next instanceof Node) || !this.element.contains(next)) {
        this.focused = null;
      }
    });
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
    if (state.kind === 'choose' && state.scope !== undefined && state.scope !== this.chosenKind) {
      this.chosenKind = state.scope;
      this.chosenIds = new Set();
    }
    this.state = state;
    this.render();
    if (moved) {
      this.focusFirst();
    }
  }

  get workspaces(): WorkspaceListState {
    return this.workspaceList;
  }

  /**
   * The Workspaces choice's list changed (#448): its known workspaces came, or a Refresh from Suno
   * began or ended. Drawn again while the choice is shown, keeping focus in the panel.
   */
  setWorkspaceList(state: WorkspaceListState): void {
    this.workspaceList = state;
    this.redrawChoice();
  }

  get playlists(): WorkspaceListState {
    return this.playlistList;
  }

  /** The Playlists choice's list changed (#449), as {@link setWorkspaceList}. */
  setPlaylistList(state: WorkspaceListState): void {
    this.playlistList = state;
    this.redrawChoice();
  }

  /** Draws the choice again while it is shown, keeping focus in the panel. */
  private redrawChoice(): void {
    if (this.state.kind !== 'choose') {
      return;
    }
    const focused = this.focused;
    const hadFocus = focused !== null && this.body.contains(focused);
    const refreshHadFocus = focused !== null && focused === this.refreshButton;
    this.render();
    if (refreshHadFocus) {
      this.refreshButton?.focus();
    } else if (hadFocus) {
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
  /** What has focus in the view, or null when nothing in it does. */
  private focused: Node | null = null;
  /** The Workspaces choice's "Refresh from Suno", while shown. */
  private refreshButton: HTMLButtonElement | null = null;

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
        return ids.length === 0 ? null : { kind: 'workspaces', ids, readTrash: this.readTrash };
      }
      case 'playlists': {
        const chosen = playlists.filter((item) => this.chosenIds.has(item.id));
        return chosen.length === 0
          ? null
          : { kind: 'playlists', playlists: chosen, readTrash: this.readTrash };
      }
    }
  }

  private render(): void {
    this.firstControl = null;
    this.refreshButton = null;
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
        this.body.append(done, images);
        if (this.state.note !== undefined) {
          this.body.append(this.make('p', { class: 'detail sync-voices' }, this.state.note));
        }
        for (const note of this.state.notes ?? []) {
          this.body.append(this.make('p', { class: 'detail sync-note' }, note));
        }
        this.body.append(again);
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
          if (kind === 'workspaces') {
            this.options.wantWorkspaces();
          } else if (kind === 'playlists') {
            this.options.wantPlaylists?.();
          }
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
      this.body.append(this.renderList(this.chosenKind), ...this.renderTrashChoice());
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
    this.renderKnownList(group, listed, kind);
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

  /**
   * The Workspaces (#448) or Playlists (#449) choice's line about its list and its "Refresh from
   * Suno". With nothing known, the line says so and the button is all there is to press.
   */
  private renderKnownList(
    group: HTMLElement,
    listed: readonly Listed[],
    kind: 'workspaces' | 'playlists',
  ): void {
    const workspaces = kind === 'workspaces';
    const state = workspaces ? this.workspaceList : this.playlistList;
    const busy = state.kind === 'loading' || state.kind === 'refreshing';
    const text =
      (workspaces
        ? workspaceListText(state, listed.length)
        : playlistListText(state, listed.length)) ??
      `These are the ${kind} n8Tracks knows, with any Suno has shown on this page. If one is missing, refresh from Suno.`;
    group.append(
      this.make(
        'p',
        state.kind === 'failed'
          ? { class: `warning sync-${kind}`, role: 'alert' }
          : { class: `detail sync-${kind}`, role: 'status' },
        text,
      ),
    );
    const refresh = this.button(
      'Refresh from Suno',
      () => {
        if (workspaces) {
          this.options.refresh();
        } else {
          this.options.refreshPlaylists?.();
        }
      },
      busy || !this.available,
    );
    refresh.setAttribute('aria-describedby', 'n8-sync-refresh-hint');
    this.refreshButton = refresh;
    group.append(
      refresh,
      this.make(
        'p',
        { class: 'detail', id: 'n8-sync-refresh-hint' },
        `Opens Library on Suno and reads your ${workspaces ? 'workspace' : 'playlist'} list; it presses nothing that changes Suno.`,
      ),
    );
  }

  /** "Also check Suno's Trash", with its hint: only for a workspace or playlist sync (#420). */
  private renderTrashChoice(): HTMLElement[] {
    const label = this.make('label', { class: 'sync-trash' });
    const box = this.make('input', {
      type: 'checkbox',
      name: 'n8-sync-trash',
      'aria-describedby': 'n8-sync-trash-hint',
    });
    box.checked = this.readTrash;
    box.disabled = !this.available;
    box.addEventListener('change', () => {
      this.readTrash = box.checked;
    });
    label.append(box, " Also check Suno's Trash");
    const hint = this.make('p', { class: 'detail', id: 'n8-sync-trash-hint' }, TRASH_HINT);
    return [label, hint];
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
