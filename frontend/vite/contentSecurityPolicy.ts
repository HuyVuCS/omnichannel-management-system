import type { Plugin } from 'vite';
import { parseOrigin } from '../src/shared/api/origin.ts';

/** The two build variables that name what the application talks to outside its own origin. */
export interface PolicyOrigins {
  /** `VITE_API_BASE_URL`: the origin of the API; empty = the origin of the page. */
  apiBaseUrl?: string;
  /** `VITE_STORAGE_ORIGIN`: the public origin of the object storage (presigned links); empty = the origin of the page. */
  storageOrigin?: string;
}

function configured(variable: string, what: string, value: string | undefined): string | null {
  const trimmed = value?.trim() ?? '';
  return trimmed === '' ? null : parseOrigin(variable, what, trimmed);
}

/**
 * The Content Security Policy of the built application. Every source is one the application really uses:
 *
 * - `script-src 'self'`: only the files of the build. No inline script, no `eval` - this is the line that makes an
 *   injected script useless (the access token is in `sessionStorage`).
 * - `style-src 'self' 'unsafe-inline'`: the stylesheet of the build, and the `<style>` elements Ant Design's CSS-in-JS
 *   adds at run time (a static host cannot issue a nonce per request).
 * - `img-src 'self' <storage>`: product images come from presigned links of the storage. No `data:` and no `blob:`:
 *   the build contains no such image and a picked file is shown only after its upload, from the storage (measured in
 *   task 026 - add a source here together with the feature that needs it).
 * - `connect-src 'self' <api> <ws of the api> <storage>`: REST and the realtime hub (WebSocket, listed by itself - a
 *   scheme is part of a source), and the upload of an image to its presigned link.
 * - `object-src 'none'`, `base-uri 'self'`, `form-action 'self'`; everything else (fonts: the application uses the
 *   fonts of the system) falls back to `default-src 'self'`.
 *
 * `frame-ancestors` cannot be set by a `<meta>` policy: the host sends it as a header (`vercel.json`).
 */
export function buildContentSecurityPolicy({ apiBaseUrl, storageOrigin }: PolicyOrigins): string {
  const api = configured('VITE_API_BASE_URL', 'the API', apiBaseUrl);
  const storage = configured('VITE_STORAGE_ORIGIN', 'the storage', storageOrigin);
  if (api !== null && storage === null) {
    // A deployment on its own origin has its storage on another one too: without it images and uploads would be
    // blocked in the browser, long after the build.
    throw new Error('VITE_STORAGE_ORIGIN is required when VITE_API_BASE_URL is set: the public origin of the storage');
  }

  const sources = (...values: (string | null)[]) => values.filter((value) => value !== null).join(' ');
  const hub = api === null ? null : api.replace(/^http/, 'ws');

  return [
    "default-src 'self'",
    "script-src 'self'",
    "style-src 'self' 'unsafe-inline'",
    `img-src ${sources("'self'", storage)}`,
    `connect-src ${sources("'self'", api, hub, storage)}`,
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
  ].join('; ');
}

/**
 * Writes the policy into `index.html` as the first element of `<head>`, at build only: the dev server needs inline
 * scripts (fast refresh) and is not what a user gets. A value that is not an origin fails the build.
 */
export function contentSecurityPolicy(): Plugin {
  let policy = '';
  return {
    name: 'omnichannel:content-security-policy',
    apply: 'build',
    configResolved(config) {
      policy = buildContentSecurityPolicy({
        apiBaseUrl: config.env.VITE_API_BASE_URL as string | undefined,
        storageOrigin: config.env.VITE_STORAGE_ORIGIN as string | undefined,
      });
    },
    transformIndexHtml() {
      return [
        {
          tag: 'meta',
          attrs: { 'http-equiv': 'Content-Security-Policy', content: policy },
          injectTo: 'head-prepend',
        },
      ];
    },
  };
}
