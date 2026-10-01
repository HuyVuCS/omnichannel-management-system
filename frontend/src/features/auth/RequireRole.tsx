import type { ReactNode } from 'react';
import { ForbiddenPage } from '@/pages/ForbiddenPage';
import { useAppSelector } from '@/shared/hooks/store';
import { selectCurrentUser } from './authSlice';
import { hasAnyRole, type Role } from './session';

interface RequireRoleProps {
  roles: readonly Role[];
  children: ReactNode;
}

/**
 * Shows the page only to users in one of `roles`; others get the 403 page. Convenience only: the API enforces the same
 * rule on every request.
 */
export function RequireRole({ roles, children }: RequireRoleProps) {
  const user = useAppSelector(selectCurrentUser);

  return hasAnyRole(user, roles) ? children : <ForbiddenPage />;
}
