/**
 * Where the API is. By default the origin of the page: the dev server and `vite preview` proxy `/api` to it. A
 * deployment that serves the frontend from another origin (README "Production deployment") sets `VITE_API_BASE_URL`
 * at build time to the origin of the API, e.g. `https://api.example.com`.
 *
 * The value is an origin: scheme, host and optional port. A trailing slash is accepted and means the same; anything
 * else - a relative address, a path, a query - is a mistake of the build and is said at once, instead of sending
 * requests somewhere unexpected.
 */
export function resolveApiOrigin(configured: string | undefined, pageOrigin: string): string {
  const value = configured?.trim() ?? '';
  if (value === '') {
    return pageOrigin;
  }

  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error('VITE_API_BASE_URL must be an absolute URL, e.g. https://api.example.com');
  }

  const isOrigin =
    (url.protocol === 'http:' || url.protocol === 'https:') &&
    url.pathname === '/' &&
    url.search === '' &&
    url.hash === '' &&
    url.username === '' &&
    url.password === '';
  if (!isOrigin) {
    throw new Error(
      'VITE_API_BASE_URL must be the origin of the API (http or https, host, optional port) without a path',
    );
  }

  return url.origin;
}

/** The address of a path of the API (`/api/...`), absolute: fetch outside a browser (tests) cannot resolve relative URLs. */
export function apiUrl(path: string): string {
  return new URL(path, resolveApiOrigin(import.meta.env.VITE_API_BASE_URL, window.location.origin)).href;
}
