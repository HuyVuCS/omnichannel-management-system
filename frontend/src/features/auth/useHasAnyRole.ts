import { useAppSelector } from '@/shared/hooks/store';
import { selectCurrentUser } from './authSlice';
import { hasAnyRole, type Role } from './session';

/**
 * True when the signed-in user has one of `roles`. Used to hide actions a role may not use; the API stays the authority
 * (a 403 is still handled).
 */
export function useHasAnyRole(roles: readonly Role[]): boolean {
  return hasAnyRole(useAppSelector(selectCurrentUser), roles);
}
