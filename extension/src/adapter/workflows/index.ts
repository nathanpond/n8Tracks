import type { Workflow } from '../workflow.ts';
import { recogniseSuno } from './recognise.ts';

/**
 * Every workflow the adapter ships, in the order the panel lists them. A story that adds a
 * workflow adds its module under this folder and its entry here.
 */
export const ADAPTER_WORKFLOWS: readonly Workflow[] = [recogniseSuno];
