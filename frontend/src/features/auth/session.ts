/** Role names as issued by the API (AspNetRoles). */
export const ROLES = ['Admin', 'Manager', 'Staff'] as const;
export type Role = (typeof ROLES)[number];

export interface AuthUser {
  id: string;
  email: string;
  roles: string[];
}

/** What a login returns, minus the token type: everything the client keeps about the signed-in user. */
export interface Session {
  accessToken: string;
  /** ISO 8601; the access token is useless after this moment (there are no refresh tokens). */
  expiresAt: string;
  user: AuthUser;
}

/**
 * sessionStorage key of the session. sessionStorage (user decision, 2026-10-01): survives a reload, is cleared when the
 * tab closes and is not shared between tabs. Revisit with httpOnly cookies in the hardening task.
 */
export const SESSION_STORAGE_KEY = 'omnichannel.session';

export function isExpired(session: Pick<Session, 'expiresAt'>, now: number = Date.now()): boolean {
  const expiresAt = Date.parse(session.expiresAt);
  return Number.isNaN(expiresAt) || expiresAt <= now;
}

function isSession(value: unknown): value is Session {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const candidate = value as Partial<Session>;
  const user = candidate.user as Partial<AuthUser> | undefined;
  return (
    typeof candidate.accessToken === 'string' &&
    candidate.accessToken.length > 0 &&
    typeof candidate.expiresAt === 'string' &&
    typeof user === 'object' &&
    user !== null &&
    typeof user.id === 'string' &&
    typeof user.email === 'string' &&
    Array.isArray(user.roles) &&
    user.roles.every((role) => typeof role === 'string')
  );
}

/** The stored session if it is well-formed and not expired; otherwise null (and the stale entry is removed). */
export function loadSession(): Session | null {
  try {
    const raw = window.sessionStorage.getItem(SESSION_STORAGE_KEY);
    if (raw === null) {
      return null;
    }

    const parsed: unknown = JSON.parse(raw);
    if (isSession(parsed) && !isExpired(parsed)) {
      return parsed;
    }

    window.sessionStorage.removeItem(SESSION_STORAGE_KEY);
    return null;
  } catch {
    // Storage blocked or the entry is not JSON: start signed out.
    return null;
  }
}

export function saveSession(session: Session | null): void {
  try {
    if (session === null) {
      window.sessionStorage.removeItem(SESSION_STORAGE_KEY);
    } else {
      window.sessionStorage.setItem(SESSION_STORAGE_KEY, JSON.stringify(session));
    }
  } catch {
    // Storage unavailable: the session then lives in memory only (lost on reload).
  }
}

export function hasAnyRole(user: AuthUser | null | undefined, roles: readonly string[]): boolean {
  return user?.roles.some((role) => roles.includes(role)) ?? false;
}
