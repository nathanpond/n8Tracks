import type { Workflow } from '../workflow.ts';
import { loadMore } from './loadMore.ts';
import { recogniseSuno } from './recognise.ts';
import { createWorkspace, moreWorkspaces, openWorkspaces, selectWorkspace } from './workspace.ts';

/**
 * Every workflow the adapter ships, in the order the panel lists them. A story that adds a
 * workflow adds its module under this folder and its entry here.
 */
export const ADAPTER_WORKFLOWS: readonly Workflow[] = [
  recogniseSuno,
  loadMore,
  openWorkspaces,
  moreWorkspaces,
  selectWorkspace,
  createWorkspace,
];
