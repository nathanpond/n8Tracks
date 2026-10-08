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
 * written to a file inside the container first, as the command reads it from a path. Writing the
 * file, seeding, and removing the file again are one `docker exec`: each exec costs a few hundred
 * milliseconds on a CI runner, and a spec that seeds three Generations paid for nine (#405).
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
  // The file is removed whether the command succeeded or not, and the command's exit status (and
  // its error output, for the failure docker() throws) is what the exec reports.
  const script =
    'printf "%s" "$1" > "$2" || exit; n8tracks seed-generation "$3" "$2"; status=$?; rm -f "$2"; exit "$status"';
  return docker('exec', container, 'sh', '-c', script, 'sh', clip, file, shortcode);
}
