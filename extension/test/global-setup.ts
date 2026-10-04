import { buildExtension } from '../scripts/lib/build.ts';

/** Builds the extension once before the tests, so they check the real `dist/` (overwriting it). */
export default async function setup(): Promise<void> {
  await buildExtension();
}
