/** The product version, split the way a Chrome manifest needs it. */
export interface ProductVersion {
  /** The whole version, such as `0.1.0` or `0.1.0-edge.abc1234`. */
  full: string;
  /** `major.minor.patch` only: what Chrome accepts as a manifest `version`. */
  numeric: string;
  /** Whether the version has a pre-release suffix. */
  isPreRelease: boolean;
}

const versionPattern = /^(\d+\.\d+\.\d+)(-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$/;

/** Chrome allows each manifest version number to be at most 65535, without leading zeros. */
function isChromeNumber(part: string): boolean {
  return String(Number(part)) === part && Number(part) <= 65535;
}

/** Parses `major.minor.patch` with an optional `-suffix`; throws naming `source` otherwise. */
export function parseProductVersion(text: string, source: string): ProductVersion {
  const full = text.trim();
  const match = versionPattern.exec(full);
  const numeric = match?.[1];
  if (!numeric?.split('.').every(isChromeNumber)) {
    throw new Error(
      `${source} must be major.minor.patch with an optional -suffix, each number at most 65535, but was '${full}'.`,
    );
  }
  return { full, numeric, isPreRelease: match?.[2] !== undefined };
}

/**
 * The version to build: `N8TRACKS_VERSION` when it is set, otherwise the root `VERSION` file.
 * An empty or whitespace-only variable counts as unset, as it does for the application.
 */
export function resolveProductVersion(
  environment: Record<string, string | undefined>,
  versionFileText: string,
): ProductVersion {
  const override = environment.N8TRACKS_VERSION?.trim();
  return override === undefined || override === ''
    ? parseProductVersion(versionFileText, 'The VERSION file')
    : parseProductVersion(override, 'N8TRACKS_VERSION');
}
