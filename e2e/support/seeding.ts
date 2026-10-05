import type { TestInfo } from '@playwright/test';
import { docker } from './containers.ts';
import { CONTAINER_BY_PROJECT } from './targets.ts';

/**
 * Attaches a Generation to the Version with `shortcode` in the project's container, with the
 * test-only `n8tracks seed-generation` command (the containers switch test seeding on), and
 * returns the new Generation's shortcode. The Version's lyrics and styles are frozen from then on.
 */
export async function seedGeneration(testInfo: TestInfo, shortcode: string): Promise<string> {
  const container = CONTAINER_BY_PROJECT[testInfo.project.name];
  if (container === undefined) {
    throw new Error(`No container is known for the project "${testInfo.project.name}".`);
  }
  return docker('exec', container, 'n8tracks', 'seed-generation', shortcode);
}
