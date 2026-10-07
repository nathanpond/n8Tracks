/**
 * The product's versioning rule (`.n8/config.yml`): the extension and n8Tracks are compatible when
 * their major and minor numbers are equal. The patch number and any pre-release suffix are ignored.
 * A version that cannot be read is never compatible.
 */

/** The numbers of `major.minor[.patch][-suffix]`. */
export interface VersionNumbers {
  major: number;
  minor: number;
  patch: number;
}

const versionPattern = /^(\d{1,9})\.(\d{1,9})(?:\.(\d{1,9}))?(?:-[0-9A-Za-z.-]+)?$/;

/** The numbers of a version, or null when it is not `major.minor[.patch][-suffix]`. */
export function parseVersion(text: string | null | undefined): VersionNumbers | null {
  const match = versionPattern.exec((text ?? '').trim());
  if (match === null) {
    return null;
  }
  return {
    major: Number(match[1]),
    minor: Number(match[2]),
    patch: match[3] === undefined ? 0 : Number(match[3]),
  };
}

/** Whether an extension of `extensionVersion` works with n8Tracks of `applicationVersion`. */
export function isCompatible(
  extensionVersion: string | null | undefined,
  applicationVersion: string | null | undefined,
): boolean {
  const extension = parseVersion(extensionVersion);
  const application = parseVersion(applicationVersion);
  return (
    extension !== null &&
    application !== null &&
    extension.major === application.major &&
    extension.minor === application.minor
  );
}

/** Which side the user should update when the two do not fit. */
export type UpdateSide = 'extension' | 'n8tracks' | 'unknown';

/** How the extension and the n8Tracks it reached fit together. */
export type Compatibility =
  | { kind: 'compatible' }
  | { kind: 'mismatch'; extensionVersion: string; applicationVersion: string; update: UpdateSide };

/**
 * Compares the two versions. The server's own answer (`compatible` in the handshake) counts too:
 * either side finding a mismatch is a mismatch, and the warning then says which side to update.
 */
export function compatibilityOf(
  extensionVersion: string,
  applicationVersion: string,
  serverSaysCompatible: boolean,
): Compatibility {
  if (serverSaysCompatible && isCompatible(extensionVersion, applicationVersion)) {
    return { kind: 'compatible' };
  }
  const extension = parseVersion(extensionVersion);
  const application = parseVersion(applicationVersion);
  let update: UpdateSide = 'unknown';
  if (extension !== null && application !== null) {
    const older =
      extension.major !== application.major
        ? extension.major < application.major
        : extension.minor < application.minor;
    const same = extension.major === application.major && extension.minor === application.minor;
    update = same ? 'unknown' : older ? 'extension' : 'n8tracks';
  }
  return { kind: 'mismatch', extensionVersion, applicationVersion, update };
}

/** The warning for a mismatch, naming both versions and which side to update; null when they fit. */
export function mismatchWarning(compatibility: Compatibility): string | null {
  if (compatibility.kind === 'compatible') {
    return null;
  }
  const { extensionVersion, applicationVersion, update } = compatibility;
  const application =
    parseVersion(applicationVersion) === null
      ? `of an unknown version (${applicationVersion})`
      : applicationVersion;
  switch (update) {
    case 'extension':
      return `Extension ${extensionVersion} is older than n8Tracks ${application}: update the extension.`;
    case 'n8tracks':
      return `Extension ${extensionVersion} is newer than n8Tracks ${application}: update n8Tracks.`;
    case 'unknown':
      return `Extension ${extensionVersion} and n8Tracks ${application} may not work together: their versions could not be compared. Update whichever is older.`;
  }
}
