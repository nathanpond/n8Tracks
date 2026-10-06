import type { ProductVersion } from './version.ts';

export type Manifest = Record<string, unknown>;

export const extensionName = 'n8Tracks';
/**
 * Exactly what the extension may ask for (the Suno integration design). `scripting` registers the
 * relay on the paired n8Tracks origin; `tabs` lets the extension find the Suno tab. The optional
 * hosts are only the ceiling of what may be requested: at pairing the extension requests exactly
 * `https://suno.com/*` and the one n8Tracks origin entered. Widening this list is a deliberate
 * change, and anything not on it fails validation (`downloads` waits for its own story).
 */
export const allowedPermissions: readonly string[] = ['storage', 'scripting', 'tabs'];
export const allowedOptionalHosts: readonly string[] = [
  'https://suno.com/*',
  'https://*/*',
  'http://*/*',
];
export const iconSizes: readonly string[] = ['16', '48', '128'];

const serviceWorkerSource = 'src/background/service-worker.ts';
export const serviceWorkerOutput = 'service-worker.js';

/** The relay content script, which the service worker registers by this path (`RELAY_FILE`). */
export const relayOutput = 'relay.js';

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Where the build puts a file the source manifest names by its source path. */
export function builtPath(sourcePath: string): string {
  if (sourcePath === serviceWorkerSource) {
    return serviceWorkerOutput;
  }
  for (const root of ['src/', 'public/']) {
    if (sourcePath.startsWith(root)) {
      return sourcePath.slice(root.length);
    }
  }
  throw new Error(`The manifest names '${sourcePath}', which is not under src/ or public/.`);
}

function builtPaths(value: unknown, what: string): Record<string, string> {
  if (!isRecord(value)) {
    throw new Error(`The source manifest has no ${what}.`);
  }
  return Object.fromEntries(
    Object.entries(value).map(([key, path]) => {
      if (typeof path !== 'string') {
        throw new Error(`The source manifest's ${what}.${key} is not a path.`);
      }
      return [key, builtPath(path)];
    }),
  );
}

/**
 * Turns the source manifest into the one written to `dist/`: source paths become built paths and
 * the version is filled in. A pre-release version keeps its full string in `version_name`.
 */
export function transformManifest(source: Manifest, version: ProductVersion): Manifest {
  const { action, background, options_ui: optionsUi } = source;
  if (!isRecord(action) || typeof action.default_popup !== 'string') {
    throw new Error('The source manifest has no action.default_popup.');
  }
  if (!isRecord(optionsUi) || typeof optionsUi.page !== 'string') {
    throw new Error('The source manifest has no options_ui.page.');
  }
  if (!isRecord(background) || typeof background.service_worker !== 'string') {
    throw new Error('The source manifest has no background.service_worker.');
  }

  const built: Manifest = {};
  for (const [key, value] of Object.entries(source)) {
    if (key === 'version_name') {
      continue;
    }
    built[key] = value;
    if (key === 'version') {
      built.version = version.numeric;
      if (version.isPreRelease) {
        built.version_name = version.full;
      }
    }
  }
  if (!('version' in source)) {
    throw new Error('The source manifest has no version placeholder.');
  }

  built.icons = builtPaths(source.icons, 'icons');
  built.action = {
    ...action,
    default_popup: builtPath(action.default_popup),
    default_icon: builtPaths(action.default_icon, 'action.default_icon'),
  };
  built.background = { ...background, service_worker: builtPath(background.service_worker) };
  built.options_ui = { ...optionsUi, page: builtPath(optionsUi.page) };
  return built;
}

function sameList(actual: unknown, expected: readonly string[]): boolean {
  return (
    Array.isArray(actual) &&
    actual.length === expected.length &&
    expected.every((item, index) => actual[index] === item)
  );
}

function checkIcons(value: unknown, what: string, problems: string[]): void {
  const sizes = isRecord(value) ? Object.keys(value) : [];
  if (
    !isRecord(value) ||
    !sameList(sizes.toSorted(), iconSizes.toSorted()) ||
    !sizes.every((size) => typeof value[size] === 'string')
  ) {
    problems.push(`${what} must have exactly the sizes ${iconSizes.join(', ')}.`);
  }
}

/**
 * The problems with a built manifest: an empty list when it asks for nothing beyond what this
 * extension is allowed. Anything that widens what the extension can reach is a problem.
 */
export function validateManifest(manifest: unknown): string[] {
  if (!isRecord(manifest)) {
    return ['The manifest must be a JSON object.'];
  }
  const problems: string[] = [];

  if (manifest.manifest_version !== 3) {
    problems.push('manifest_version must be 3.');
  }
  if (manifest.name !== extensionName) {
    problems.push(`name must be '${extensionName}'.`);
  }
  if (
    typeof manifest.version !== 'string' ||
    !/^\d+\.\d+\.\d+$/.test(manifest.version) ||
    manifest.version === '0.0.0'
  ) {
    problems.push('version must be the numeric major.minor.patch written by the build.');
  }
  if (
    'version_name' in manifest &&
    (typeof manifest.version_name !== 'string' ||
      typeof manifest.version !== 'string' ||
      !manifest.version_name.startsWith(`${manifest.version}-`))
  ) {
    problems.push('version_name, when present, must be the version followed by a -suffix.');
  }
  if (!sameList(manifest.permissions, allowedPermissions)) {
    problems.push(`permissions must be exactly ${JSON.stringify(allowedPermissions)}.`);
  }
  if (!sameList(manifest.optional_host_permissions, allowedOptionalHosts)) {
    problems.push(
      `optional_host_permissions must be exactly ${JSON.stringify(allowedOptionalHosts)}.`,
    );
  }
  for (const forbidden of [
    'host_permissions',
    'optional_permissions',
    'content_scripts',
    'externally_connectable',
  ]) {
    if (forbidden in manifest) {
      problems.push(`${forbidden} must be absent.`);
    }
  }

  const { action, background, options_ui: optionsUi } = manifest;
  if (
    !isRecord(optionsUi) ||
    typeof optionsUi.page !== 'string' ||
    optionsUi.open_in_tab !== true ||
    Object.keys(optionsUi).length !== 2
  ) {
    problems.push('options_ui must name the options page, opened in a tab, and nothing else.');
  }
  if (
    !isRecord(background) ||
    typeof background.service_worker !== 'string' ||
    background.type !== 'module'
  ) {
    problems.push('background must name a module service worker.');
  }
  if (!isRecord(action) || typeof action.default_popup !== 'string') {
    problems.push('action.default_popup must name the popup page.');
  }
  checkIcons(manifest.icons, 'icons', problems);
  checkIcons(isRecord(action) ? action.default_icon : undefined, 'action.default_icon', problems);

  return problems;
}

/** Every file the built manifest refers to, relative to `dist/`. */
export function referencedFiles(manifest: Manifest): string[] {
  const { action, background, icons, options_ui: optionsUi } = manifest;
  const files: unknown[] = [];
  if (isRecord(background)) {
    files.push(background.service_worker);
  }
  if (isRecord(optionsUi)) {
    files.push(optionsUi.page);
  }
  if (isRecord(action)) {
    files.push(action.default_popup);
    if (isRecord(action.default_icon)) {
      files.push(...Object.values(action.default_icon));
    }
  }
  if (isRecord(icons)) {
    files.push(...Object.values(icons));
  }
  return [...new Set(files.filter((file) => typeof file === 'string'))];
}
