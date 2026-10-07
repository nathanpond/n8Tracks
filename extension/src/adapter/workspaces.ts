import type { ObservedMessage } from './observed.ts';
import type { Target } from './primitives.ts';

/**
 * Suno's workspaces as Generate on Suno reads them (#145, TS-003). The workspace list is
 * `GET /api/project/me?page=N`, 20 per page, and is complete when the pages seen hold its
 * `num_total_results`. Suno's rows carry no ID, so a workspace is matched to its row by the name
 * Suno's own list gives for its ID; two rows that would match are never chosen between. Creating
 * one sends `POST /api/project`, whose answer gives the new workspace's ID.
 */

/** Workspaces per page of `/api/project/me` (TS-003). */
export const WORKSPACES_PER_PAGE = 20;

/** The most pages read before the list is taken as one that does not end. */
export const MAXIMUM_WORKSPACE_PAGES = 50;

/** A workspace as Suno lists it: its ID, its name, and the raw project, for n8Tracks' record. */
export interface ListedWorkspace {
  id: string;
  name: string;
  raw: Record<string, unknown>;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** The workspace a raw project describes, or null when it has no ID. */
function workspaceOf(project: unknown): ListedWorkspace | null {
  return isRecord(project) && typeof project.id === 'string' && project.id !== ''
    ? { id: project.id, name: typeof project.name === 'string' ? project.name : '', raw: project }
    : null;
}

/**
 * Suno's workspace list as it pages in. A page that comes again (the page asks for its first page
 * whenever the list opens) replaces the one before; the list is complete once pages 1 to N are all
 * here and hold `num_total_results`.
 */
export class WorkspaceList {
  private readonly pages = new Map<number, ListedWorkspace[]>();
  private total: number | null = null;

  /** Takes a response of the list; false when it is not one (no page number or total). */
  add(message: ObservedMessage): boolean {
    const body = message.body;
    if (
      message.kind !== 'workspaces' ||
      !isRecord(body) ||
      typeof body.current_page !== 'number' ||
      typeof body.num_total_results !== 'number' ||
      !Array.isArray(body.projects)
    ) {
      return false;
    }
    this.pages.set(
      body.current_page,
      body.projects.map(workspaceOf).filter((item): item is ListedWorkspace => item !== null),
    );
    this.total = body.num_total_results;
    return true;
  }

  /** How many pages in a row, from the first, are here. */
  get pagesInOrder(): number {
    let count = 0;
    while (this.pages.has(count + 1)) {
      count += 1;
    }
    return count;
  }

  /** Whether the whole list is here: every page from the first, holding the total Suno gave. */
  get complete(): boolean {
    return (
      this.total !== null &&
      this.pagesInOrder > 0 &&
      this.pagesInOrder * WORKSPACES_PER_PAGE >= this.total
    );
  }

  /** Every workspace read so far, in Suno's order, each once. */
  workspaces(): ListedWorkspace[] {
    const seen = new Map<string, ListedWorkspace>();
    for (const number of [...this.pages.keys()].toSorted((a, b) => a - b)) {
      for (const workspace of this.pages.get(number) ?? []) {
        seen.set(workspace.id, workspace);
      }
    }
    return [...seen.values()];
  }
}

/** The ID of the workspace whose songs a library-feed request asked for, or null. */
export function workspaceOfFeed(message: ObservedMessage): string | null {
  const filters = message.request.filters;
  const workspace = isRecord(filters) ? filters.workspace : undefined;
  return isRecord(workspace) && typeof workspace.workspaceId === 'string'
    ? workspace.workspaceId
    : null;
}

/** The workspace `POST /api/project` created, or null when the answer does not say. */
export function createdWorkspaceOf(message: ObservedMessage): ListedWorkspace | null {
  return message.kind === 'workspace-created' ? workspaceOf(message.body) : null;
}

function collapse(text: string): string {
  return text.replace(/\s+/g, ' ').trim();
}

/** `text` as a regular expression that matches it literally. */
function literally(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * The row of the workspace named `name` in Suno's list (TS-003: `page.workspace-selector.html`):
 * a `role=button` whose name is the workspace's name, then its song count and age. Null for a
 * blank name, which no row can be found by. A longer name that starts with this one matches too,
 * so `find` then reports two and nothing is chosen. The name is never part of the description,
 * which reaches the diagnostic report.
 */
export function workspaceRow(name: string): Target | null {
  const wanted = collapse(name);
  return wanted === ''
    ? null
    : {
        role: 'button',
        name: new RegExp(`^${literally(wanted)}(?: |$)`),
        description: "the workspace's row in Suno's workspace list",
      };
}

/** The workspaces in `list` with the name `name`, compared as Suno shows it. */
export function namedLike(list: readonly ListedWorkspace[], name: string): ListedWorkspace[] {
  const wanted = collapse(name);
  return list.filter((workspace) => collapse(workspace.name) === wanted);
}

/**
 * The name a workspace created for a Song is given: the Song's title, as Suno would show it. The
 * create row shows no limit (TS-003: `page.create-workspace-dialog.html` has no `maxlength`), so
 * the title is not cut; "Untitled" stands in for a blank one, as the field's own placeholder does.
 */
export function workspaceNameFor(title: string): string {
  return collapse(title) === '' ? 'Untitled' : collapse(title);
}

/** A workspace offered in the panel's choice, with the n8Tracks Songs already in it. */
export interface WorkspaceOption {
  id: string;
  name: string;
  /** How many n8Tracks Songs are in it; 0 when n8Tracks associates none with it. */
  songCount: number;
  /** It has the Song's title as its name and no Song: offered first. */
  sameName: boolean;
}

/**
 * The existing workspaces the panel offers for a Song titled `title`: those with the same name and
 * no Song first, then the others by name, each with its Song count from n8Tracks (`counts`, by
 * Suno ID). `exclude` (the Song's own workspace, when it went missing) is left out.
 */
export function workspaceOptions(
  list: readonly ListedWorkspace[],
  title: string,
  counts: ReadonlyMap<string, number>,
  exclude: string | null = null,
): WorkspaceOption[] {
  const wanted = collapse(title);
  const options = list
    .filter((workspace) => workspace.id !== exclude)
    .map((workspace) => {
      const songCount = counts.get(workspace.id) ?? 0;
      return {
        id: workspace.id,
        name: workspace.name,
        songCount,
        sameName: collapse(workspace.name) === wanted && songCount === 0,
      };
    });
  const byName = (a: WorkspaceOption, b: WorkspaceOption) =>
    a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }) || a.id.localeCompare(b.id);
  return [
    ...options.filter((option) => option.sameName).toSorted(byName),
    ...options.filter((option) => !option.sameName).toSorted(byName),
  ];
}
