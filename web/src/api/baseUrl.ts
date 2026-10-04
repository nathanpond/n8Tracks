/**
 * The backend serves the shell with `<base href="<base path>/">`, so `document.baseURI` is the
 * root of the app wherever it is hosted. Every request and route is resolved against it.
 */
export function resolveAppUrl(relativePath: string): URL {
  return new URL(relativePath, document.baseURI);
}

/** The path the app is served under, without a trailing slash: `/` at the root of a hostname. */
export function appBasePath(): string {
  const path = new URL(document.baseURI).pathname.replace(/\/+$/, '');
  return path === '' ? '/' : path;
}
