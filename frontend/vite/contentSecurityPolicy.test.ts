import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { build } from 'vite';
import { afterEach, describe, expect, it } from 'vitest';
import { buildContentSecurityPolicy, contentSecurityPolicy } from './contentSecurityPolicy';

const directive = (policy: string, name: string) =>
  policy
    .split('; ')
    .find((part) => part.startsWith(`${name} `))
    ?.slice(name.length + 1);

describe('the policy of the built application', () => {
  it('names the API, its WebSocket address and the storage when they are on other origins', () => {
    const policy = buildContentSecurityPolicy({
      apiBaseUrl: 'https://api.example.test/',
      storageOrigin: ' https://api.example.test:8443 ',
    });

    expect(policy).toBe(
      "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' https://api.example.test:8443; " +
        "connect-src 'self' https://api.example.test wss://api.example.test https://api.example.test:8443; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'",
    );
  });

  it('uses ws for an API without TLS (a local stack)', () => {
    const policy = buildContentSecurityPolicy({
      apiBaseUrl: 'http://localhost:18080',
      storageOrigin: 'http://localhost:18081',
    });

    expect(directive(policy, 'connect-src')).toBe(
      "'self' http://localhost:18080 ws://localhost:18080 http://localhost:18081",
    );
  });

  it('allows only the origin of the page when nothing is configured', () => {
    const policy = buildContentSecurityPolicy({ apiBaseUrl: '', storageOrigin: undefined });

    expect(directive(policy, 'connect-src')).toBe("'self'");
    expect(directive(policy, 'img-src')).toBe("'self'");
  });

  it('accepts a storage on another origin while the API is on the origin of the page', () => {
    const policy = buildContentSecurityPolicy({ storageOrigin: 'http://127.0.0.1:8333' });

    expect(directive(policy, 'connect-src')).toBe("'self' http://127.0.0.1:8333");
    expect(directive(policy, 'img-src')).toBe("'self' http://127.0.0.1:8333");
  });

  it.each([[{}], [{ apiBaseUrl: 'https://api.example.test', storageOrigin: 'https://files.example.test' }]])(
    'never allows inline or evaluated scripts (%o)',
    (origins) => {
      const policy = buildContentSecurityPolicy(origins);

      expect(directive(policy, 'script-src')).toBe("'self'");
      expect(policy).not.toContain('unsafe-eval');
      expect(policy.replace("style-src 'self' 'unsafe-inline'", '')).not.toContain('unsafe-inline');
      expect(policy).not.toContain('*');
      // No scheme source: a scheme alone would allow every origin of that scheme.
      expect(policy).not.toMatch(/(^|\s)(data|blob|https?|wss?):(\s|;|$)/);
    },
  );

  it('refuses an API on another origin without the origin of the storage', () => {
    expect(() => buildContentSecurityPolicy({ apiBaseUrl: 'https://api.example.test' })).toThrow(/VITE_STORAGE_ORIGIN/);
    expect(() => buildContentSecurityPolicy({ apiBaseUrl: 'https://api.example.test', storageOrigin: '  ' })).toThrow(
      /VITE_STORAGE_ORIGIN is required/,
    );
  });

  it.each([
    ['a path', 'https://files.example.test/bucket'],
    ['a host without a scheme', 'files.example.test'],
    ['a query', 'https://files.example.test/?x=1'],
    ['credentials', 'https://user:secret@files.example.test'],
    ['another scheme', 'ftp://files.example.test'],
    ['a second source', "https://files.example.test 'unsafe-inline'"],
    ['the end of the directive', 'https://files.example.test; script-src *'],
  ])('refuses a storage origin with %s, without repeating it', (_what, value) => {
    const build = () => buildContentSecurityPolicy({ apiBaseUrl: 'https://api.example.test', storageOrigin: value });

    expect(build).toThrow(/VITE_STORAGE_ORIGIN/);
    expect(build).not.toThrow(/secret/);
  });

  it('refuses an API address that is not an origin', () => {
    expect(() =>
      buildContentSecurityPolicy({
        apiBaseUrl: 'https://api.example.test/api',
        storageOrigin: 'https://f.example.test',
      }),
    ).toThrow(/VITE_API_BASE_URL/);
  });
});

describe('the build', () => {
  let root = '';

  afterEach(() => {
    if (root) {
      rmSync(root, { recursive: true, force: true });
    }
  });

  /** A build of a page of its own with the plugin and the given variables; returns the written index.html. */
  async function buildWith(variables: Record<string, string>): Promise<string> {
    root = mkdtempSync(join(tmpdir(), 'omnichannel-csp-'));
    writeFileSync(
      join(root, 'index.html'),
      '<!doctype html><html><head><meta charset="UTF-8" /><title>t</title></head><body><script type="module" src="/main.js"></script></body></html>',
    );
    writeFileSync(join(root, 'main.js'), 'document.title = "built";\n');
    writeFileSync(
      join(root, '.env'),
      Object.entries(variables)
        .map(([name, value]) => `${name}=${value}\n`)
        .join(''),
    );

    await build({ root, configFile: false, logLevel: 'silent', plugins: [contentSecurityPolicy()] });
    // Vite writes an apostrophe inside an attribute as a character reference; a browser reads it back as the apostrophe.
    return readFileSync(join(root, 'dist', 'index.html'), 'utf8').replaceAll('&#39;', "'");
  }

  it('writes the policy as the first element of the head, from the build variables', async () => {
    const html = await buildWith({
      VITE_API_BASE_URL: 'https://api.example.test',
      VITE_STORAGE_ORIGIN: 'https://api.example.test:8443',
    });

    const expected = buildContentSecurityPolicy({
      apiBaseUrl: 'https://api.example.test',
      storageOrigin: 'https://api.example.test:8443',
    });
    expect(html).toContain(`<meta http-equiv="Content-Security-Policy" content="${expected}">`);
    expect(html.indexOf('http-equiv="Content-Security-Policy"')).toBeLessThan(html.indexOf('<script'));
    expect(html.indexOf('<head>') + '<head>'.length).toBe(html.search(/\s*<meta http-equiv/));
  });

  it('fails when a variable is not an origin', async () => {
    await expect(
      buildWith({ VITE_API_BASE_URL: 'https://api.example.test', VITE_STORAGE_ORIGIN: 'https://files.example.test/x' }),
    ).rejects.toThrow(/VITE_STORAGE_ORIGIN/);
  });

  it('is a plugin of the build only: the dev server serves index.html without a policy', () => {
    expect(contentSecurityPolicy().apply).toBe('build');
  });
});
