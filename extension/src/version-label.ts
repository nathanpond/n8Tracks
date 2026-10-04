/** The parts of the extension manifest the popup shows. */
export interface ManifestIdentity {
  name: string;
  version: string;
  version_name?: string | undefined;
}

/** The label shown for a version: `v0.1.0`. */
export function versionLabel(version: string): string {
  return `v${version}`;
}

/**
 * The version to show. A pre-release build carries the full string in `version_name`, because
 * Chrome's `version` holds only the numeric part.
 */
export function displayVersion(manifest: ManifestIdentity): string {
  return manifest.version_name ?? manifest.version;
}
