import { parseOrigin } from './origin';

/**
 * Where the API is. By default the origin of the page: the dev server and `vite preview` proxy `/api` to it. A
 * deployment that serves the frontend from another origin (docs/operations-guide.md "Production deployment") sets `VITE_API_BASE_URL`
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

  return parseOrigin('VITE_API_BASE_URL', 'the API', value);
}

/** The address of a path of the API (`/api/...`), absolute: fetch outside a browser (tests) cannot resolve relative URLs. */
export function apiUrl(path: string): string {
  return new URL(path, resolveApiOrigin(import.meta.env.VITE_API_BASE_URL, window.location.origin)).href;
}
