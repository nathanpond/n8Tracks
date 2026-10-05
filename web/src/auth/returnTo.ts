/** The route of the sign-in page, relative to the app's base path. */
export const SIGN_IN_ROUTE = 'sign-in';

/** The query parameter carrying where to go after signing in. */
export const RETURN_TO_PARAMETER = 'returnTo';

/**
 * `value` when it is a path inside the app (`/songs?page=2`), otherwise `/`. Anything that could
 * leave the app is refused: an absolute URL, a scheme-relative `//host`, a backslash (which some
 * browsers read as a slash), or a control character.
 */
export function safeReturnTo(value: string | null): string {
  if (value === null || !value.startsWith('/') || value.startsWith('//')) {
    return '/';
  }
  for (const character of value) {
    const code = character.charCodeAt(0);
    if (character === '\\' || code < 0x20 || code === 0x7f) {
      return '/';
    }
  }
  return value;
}

/** The sign-in route for someone who asked for `path` (router-relative, with query and hash). */
export function signInPath(path: string): string {
  const target = safeReturnTo(path);
  return target === '/'
    ? `/${SIGN_IN_ROUTE}`
    : `/${SIGN_IN_ROUTE}?${RETURN_TO_PARAMETER}=${encodeURIComponent(target)}`;
}
