import type { ObservedMessage } from './observed.ts';

/**
 * Suno's workspaces as Generate on Suno reads them (#145, TS-003, TS-007-15). The workspace list
 * is `GET /api/project/me?page=N`, 20 per page, and Suno loads a further page only on a trusted
 * scroll by the user (TS-007-15): the extension cannot page through it, so Generate never needs the
 * whole list. It takes the workspaces of every list answer the page gets (the pages Suno loaded,
 * and the answers to a search typed into the list's search box), each once by ID. A workspace is
 * opened by its address (`/create?wid=<id>`), never by its row, whose name may be another's.
 * Creating one sends `POST /api/project`, whose answer gives the new workspace's ID.
 */

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
 * The workspaces Suno has shown the page so far: every project of every list answer taken, in the
 * order first seen, each once by ID (a later answer's copy replaces the earlier, so a rename
 * follows). Never complete: what Suno did not load is not here (TS-007-15).
 */
export class KnownWorkspaces {
  private readonly byId = new Map<string, ListedWorkspace>();

  /** Takes a list answer (a page or a search's); false when it is not one (no list of projects). */
  add(message: ObservedMessage): boolean {
    const body = message.body;
    if (message.kind !== 'workspaces' || !isRecord(body) || !Array.isArray(body.projects)) {
      return false;
    }
    for (const workspace of body.projects.map(workspaceOf)) {
      if (workspace !== null) {
        this.byId.set(workspace.id, workspace);
      }
    }
    return true;
  }

  /** Every workspace known, in the order first seen. */
  workspaces(): ListedWorkspace[] {
    return [...this.byId.values()];
  }

  /** The workspace with this ID, if known. */
  get(id: string): ListedWorkspace | undefined {
    return this.byId.get(id);
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

/** The workspaces in `list` with the name `name`, compared as Suno shows it. */
export function namedLike(list: readonly ListedWorkspace[], name: string): ListedWorkspace[] {
  const wanted = collapse(name);
  return list.filter((workspace) => collapse(workspace.name) === wanted);
}

/**
 * Whether `name` holds `query`, compared without regard to case or runs of spaces: how the panel
 * narrows the workspaces it offers to a search. A blank query holds every name.
 */
export function nameHolds(name: string, query: string): boolean {
  return collapse(name).toLocaleLowerCase().includes(collapse(query).toLocaleLowerCase());
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
