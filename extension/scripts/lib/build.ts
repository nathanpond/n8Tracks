import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'vite';
import {
  referencedFiles,
  relayOutput,
  serviceWorkerOutput,
  transformManifest,
  validateManifest,
  type Manifest,
} from './manifest.ts';
import { parseProductVersion, resolveProductVersion, type ProductVersion } from './version.ts';

export const extensionRoot = fileURLToPath(new URL('../..', import.meta.url));
export const distDirectory = join(extensionRoot, 'dist');
const versionFile = join(extensionRoot, '..', 'VERSION');

function readJson(path: string): unknown {
  return JSON.parse(readFileSync(path, 'utf8'));
}

function versionFileText(): string {
  if (!existsSync(versionFile)) {
    throw new Error(`The VERSION file is missing at ${versionFile}.`);
  }
  return readFileSync(versionFile, 'utf8');
}

/** The version this build is stamped with: `N8TRACKS_VERSION`, or the root `VERSION` file. */
export function productVersion(): ProductVersion {
  return resolveProductVersion(process.env, versionFileText());
}

/**
 * Keeps the `version` in package.json and package-lock.json equal to the `VERSION` file. The
 * `N8TRACKS_VERSION` override is left out on purpose: a one-off build must not change tracked
 * files.
 */
function syncPackageVersion(): void {
  const version = parseProductVersion(versionFileText(), 'The VERSION file').full;
  for (const name of ['package.json', 'package-lock.json']) {
    const path = join(extensionRoot, name);
    const before = readFileSync(path, 'utf8');
    const after = before.replace(/^( {2}"version": ")[^"]*(")/m, `$1${version}$2`);
    const root = /^( {4}"": \{\n {6}"name": "[^"]*",\n {6}"version": ")[^"]*(")/m;
    const synced = name === 'package-lock.json' ? after.replace(root, `$1${version}$2`) : after;
    if (synced !== before) {
      writeFileSync(path, synced);
    }
  }
}

/** Cleans `dist/`, bundles the popup and the service worker, and writes the built manifest. */
export async function buildExtension(): Promise<ProductVersion> {
  const version = productVersion();
  syncPackageVersion();

  await build({
    configFile: false,
    root: join(extensionRoot, 'src'),
    publicDir: join(extensionRoot, 'public'),
    // Relative URLs: an extension page is served from chrome-extension://<id>/, not a site root.
    base: './',
    logLevel: 'warn',
    build: {
      outDir: distDirectory,
      emptyOutDir: true,
      // The preload polyfill is for old browsers; extension pages always run in current Chrome.
      modulePreload: { polyfill: false },
      rolldownOptions: {
        input: {
          popup: join(extensionRoot, 'src/popup/popup.html'),
          options: join(extensionRoot, 'src/options/options.html'),
          'service-worker': join(extensionRoot, 'src/background/service-worker.ts'),
        },
        output: {
          // The manifest names the service worker, so its file name cannot carry a hash.
          entryFileNames: (chunk) =>
            chunk.name === 'service-worker' ? serviceWorkerOutput : 'assets/[name]-[hash].js',
        },
      },
    },
  });

  // The relay is a content script, which runs as a classic script: one file, no imports, so it
  // is built on its own. The service worker registers it by this name for the paired origin.
  await build({
    configFile: false,
    root: join(extensionRoot, 'src'),
    publicDir: false,
    logLevel: 'warn',
    build: {
      outDir: distDirectory,
      emptyOutDir: false,
      rolldownOptions: {
        input: { relay: join(extensionRoot, 'src/content/relay-main.ts') },
        output: { format: 'iife', entryFileNames: relayOutput },
      },
    },
  });
  if (!existsSync(join(distDirectory, relayOutput))) {
    throw new Error(`The build did not make ${relayOutput}.`);
  }

  const source = readJson(join(extensionRoot, 'manifest.json')) as Manifest;
  const manifest = transformManifest(source, version);
  const problems = validateManifest(manifest);
  const missing = referencedFiles(manifest).filter(
    (file) => !existsSync(join(distDirectory, file)),
  );
  problems.push(
    ...missing.map((file) => `The manifest names ${file}, which the build did not make.`),
  );
  if (problems.length > 0) {
    throw new Error(`The built manifest is not valid:\n- ${problems.join('\n- ')}`);
  }
  writeFileSync(join(distDirectory, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  return version;
}
