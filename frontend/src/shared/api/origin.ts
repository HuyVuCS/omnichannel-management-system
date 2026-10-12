/**
 * An origin named by a build variable: scheme, host and optional port. A trailing slash is accepted and means the
 * same; anything else - a relative address, a path, a query, credentials - is a mistake of the build and is said at
 * once. The message names the variable, never its value (it could carry credentials).
 *
 * Used by the application (`apiOrigin.ts`) and by the build (`vite/contentSecurityPolicy.ts`): no browser API here.
 */
export function parseOrigin(variable: string, what: string, value: string): string {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error(`${variable} must be an absolute URL, e.g. https://api.example.com`);
  }

  const isOrigin =
    (url.protocol === 'http:' || url.protocol === 'https:') &&
    url.pathname === '/' &&
    url.search === '' &&
    url.hash === '' &&
    url.username === '' &&
    url.password === '';
  if (!isOrigin) {
    throw new Error(`${variable} must be the origin of ${what} (http or https, host, optional port) without a path`);
  }

  return url.origin;
}
