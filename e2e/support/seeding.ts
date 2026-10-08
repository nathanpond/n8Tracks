import { randomUUID } from 'node:crypto';
import type { TestInfo } from '@playwright/test';
import { docker } from './containers.ts';
import { CONTAINER_BY_PROJECT } from './targets.ts';

/** The project's container, which every seeding command runs in. */
function containerOf(testInfo: TestInfo): string {
  const container = CONTAINER_BY_PROJECT[testInfo.project.name];
  if (container === undefined) {
    throw new Error(`No container is known for the project "${testInfo.project.name}".`);
  }
  return container;
}

/**
 * Attaches a Generation to the Version with `shortcode` in the project's container, with the
 * test-only `n8tracks seed-generation` command (the containers switch test seeding on), and
 * returns the new Generation's shortcode. The Version's lyrics and styles are frozen from then on.
 * With `clip` (the text of one Suno clip object), the Generation keeps that clip: the text is
 * written to a file inside the container first, as the command reads it from a path.
 */
export function seedGeneration(
  testInfo: TestInfo,
  shortcode: string,
  clip?: string,
): Promise<string> {
  return seedGenerationIn(containerOf(testInfo), shortcode, clip);
}

/** As {@link seedGeneration}, in the container named `container` (one a test started itself). */
export async function seedGenerationIn(
  container: string,
  shortcode: string,
  clip?: string,
): Promise<string> {
  if (clip === undefined) {
    return docker('exec', container, 'n8tracks', 'seed-generation', shortcode);
  }
  const file = `/tmp/seed-clip-${randomUUID()}.json`;
  await docker('exec', container, 'sh', '-c', 'printf "%s" "$1" > "$2"', 'sh', clip, file);
  try {
    return await docker('exec', container, 'n8tracks', 'seed-generation', shortcode, file);
  } finally {
    await docker('exec', container, 'rm', '-f', file);
  }
}
