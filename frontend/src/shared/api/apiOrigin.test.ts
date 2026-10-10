import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { hubUrl } from '@/features/realtime/signalrHub';
import { adminUser, server } from '@/test/server';
import { apiUrl, resolveApiOrigin } from './apiOrigin';

const PAGE = 'https://app.example.test';

afterEach(() => {
  vi.unstubAllEnvs();
  vi.resetModules();
});

describe('the origin of the API', () => {
  it('is the origin of the page when nothing is configured', () => {
    expect(resolveApiOrigin(undefined, PAGE)).toBe(PAGE);
    expect(resolveApiOrigin('', PAGE)).toBe(PAGE);
    expect(resolveApiOrigin('   ', PAGE)).toBe(PAGE);
  });

  it('is the configured origin, with or without a trailing slash', () => {
    expect(resolveApiOrigin('https://api.example.test', PAGE)).toBe('https://api.example.test');
    expect(resolveApiOrigin('https://api.example.test/', PAGE)).toBe('https://api.example.test');
    expect(resolveApiOrigin(' http://localhost:8080/ ', PAGE)).toBe('http://localhost:8080');
    expect(resolveApiOrigin('https://API.Example.test:8443', PAGE)).toBe('https://api.example.test:8443');
  });

  it.each([
    ['a relative address', '/api'],
    ['a host without a scheme', 'api.example.test'],
    ['a path', 'https://api.example.test/api'],
    ['a path with a trailing slash', 'https://api.example.test/v1/'],
    ['a query', 'https://api.example.test/?x=1'],
    ['a fragment', 'https://api.example.test/#x'],
    ['credentials', 'https://user:secret@api.example.test'],
    ['another scheme', 'ftp://api.example.test'],
  ])('refuses %s instead of guessing', (_what, value) => {
    expect(() => resolveApiOrigin(value, PAGE)).toThrow(/VITE_API_BASE_URL/);
    // The message never repeats the value (it could carry credentials).
    expect(() => resolveApiOrigin(value, PAGE)).not.toThrow(/secret/);
  });
});

describe('addresses of the API and of the hub', () => {
  it('are on the origin of the page by default', () => {
    expect(apiUrl('/api')).toBe(`${window.location.origin}/api`);
    expect(hubUrl()).toBe(`${window.location.origin}/api/hubs/notifications`);
  });

  it.each(['https://api.example.test', 'https://api.example.test/'])(
    'are on the configured origin (%s): no doubled and no missing slash',
    (configured) => {
      vi.stubEnv('VITE_API_BASE_URL', configured);

      expect(apiUrl('/api')).toBe('https://api.example.test/api');
      expect(hubUrl()).toBe('https://api.example.test/api/hubs/notifications');
    },
  );

  it('every request of the application goes to the configured origin, with the token in the Authorization header', async () => {
    vi.stubEnv('VITE_API_BASE_URL', 'https://api.example.test/');
    vi.resetModules();
    const seen: { url: string; authorization: string | null }[] = [];
    server.use(
      http.get('https://api.example.test/api/auth/me', ({ request }) => {
        seen.push({ url: request.url, authorization: request.headers.get('Authorization') });
        return HttpResponse.json(adminUser);
      }),
    );
    // Fresh modules: the base address of the API client is fixed when it is created.
    const { saveSession } = await import('@/features/auth/session');
    saveSession({ accessToken: 'token-1', expiresAt: new Date(Date.now() + 60_000).toISOString(), user: adminUser });
    const { createAppStore } = await import('@/app/store');
    const { authApi } = await import('@/features/auth/authApi');
    const store = createAppStore();

    const result = await store.dispatch(authApi.endpoints.me.initiate());

    expect(result.data).toEqual(adminUser);
    expect(seen).toEqual([{ url: 'https://api.example.test/api/auth/me', authorization: 'Bearer token-1' }]);
    saveSession(null);
  });
});
