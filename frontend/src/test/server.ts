import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import type { AuthUser, Session } from '@/features/auth/session';
import type { ProblemDetails } from '@/shared/api/types';

/** Path pattern of an API endpoint for MSW, e.g. `api('/users')` → any origin + `/api/users`. */
export function api(path: string): string {
  return `*/api${path}`;
}

export const adminUser: AuthUser = { id: 'user-admin', email: 'admin@example.com', roles: ['Admin'] };
export const managerUser: AuthUser = { id: 'user-manager', email: 'manager@example.com', roles: ['Manager'] };
export const staffUser: AuthUser = { id: 'user-staff', email: 'staff@example.com', roles: ['Staff'] };

/** A session as stored after login; valid for an hour unless `expiresInMs` says otherwise. */
export function sessionOf(user: AuthUser, expiresInMs: number = 60 * 60 * 1000): Session {
  return {
    accessToken: `test-token-${user.id}`,
    expiresAt: new Date(Date.now() + expiresInMs).toISOString(),
    user,
  };
}

/** A problem document as the API writes it. */
export function problem(status: number, body: Partial<ProblemDetails> = {}) {
  return HttpResponse.json(
    { status, title: 'Problem', correlationId: 'corr-from-server', ...body },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

/** `GET /api/dashboard/summary` without anything in it (the fixtures with numbers are in `dashboard.ts`). */
const EMPTY_SUMMARY = {
  generatedAt: '2026-10-02T01:05:09+00:00',
  from: '2026-10-01T01:05:09+00:00',
  to: '2026-10-02T01:05:09+00:00',
  orders: { total: 0, byStatus: [], byChannel: [], salesAmount: 0 },
  inventory: { products: 0, lowStock: 0, outOfStock: 0 },
  processing: [],
  stockSync: [],
  outbox: { pending: 0, failed: 0, oldestPendingAt: null },
  alerts: { open: 0, acknowledged: 0, byType: [] },
};

const users: Record<string, AuthUser> = {
  [`Bearer ${sessionOf(adminUser).accessToken}`]: adminUser,
  [`Bearer ${sessionOf(managerUser).accessToken}`]: managerUser,
  [`Bearer ${sessionOf(staffUser).accessToken}`]: staffUser,
};

/**
 * Default handlers: the session check every signed-in page makes, and an empty summary for the landing page (many
 * tests only pass the dashboard on their way; its own tests serve their numbers). Everything else must be declared by
 * the test (`server.use`), and a request without a handler fails the test.
 */
export const server = setupServer(
  http.get(api('/auth/me'), ({ request }) => {
    const user = users[request.headers.get('Authorization') ?? ''];
    return user ? HttpResponse.json(user) : problem(401);
  }),
  http.get(api('/dashboard/summary'), () => HttpResponse.json(EMPTY_SUMMARY)),
);
