import { readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { zipSync, type Zippable } from 'fflate';

const zipPrefix = 'n8tracks-extension-';

export function zipFileName(version: string): string {
  return `${zipPrefix}${version}.zip`;
}

function filesUnder(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true, recursive: true })
    .filter((entry) => entry.isFile())
    .map((entry) => join(entry.parentPath, entry.name))
    .sort();
}

/**
 * Zips the contents of `distDirectory` (not the folder itself) into
 * `<outputDirectory>/n8tracks-extension-<version>.zip`, after deleting the zips of other versions
 * so a stale one is never uploaded by mistake. Returns the path of the new zip.
 */
export function packageExtension(
  distDirectory: string,
  outputDirectory: string,
  version: string,
): string {
  for (const name of readdirSync(outputDirectory)) {
    if (name.startsWith(zipPrefix) && name.endsWith('.zip')) {
      rmSync(join(outputDirectory, name));
    }
  }

  const entries: Zippable = {};
  for (const file of filesUnder(distDirectory)) {
    // Zip entry names always use forward slashes.
    entries[relative(distDirectory, file).split(sep).join('/')] = readFileSync(file);
  }
  if (!('manifest.json' in entries)) {
    throw new Error(`${distDirectory} has no manifest.json; run the build first.`);
  }

  const zipPath = join(outputDirectory, zipFileName(version));
  writeFileSync(zipPath, zipSync(entries, { level: 9 }));
  return zipPath;
}
