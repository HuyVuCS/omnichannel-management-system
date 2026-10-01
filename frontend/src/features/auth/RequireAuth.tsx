import { Navigate, Outlet, useLocation } from 'react-router';
import { useAppSelector } from '@/shared/hooks/store';
import { selectSession, selectSignedOutByUser } from './authSlice';
import { LOGIN_PATH, loginLocation } from './returnUrl';

/**
 * Renders the nested routes for a signed-in user. Without a session (never signed in, token expired, or a 401 from the
 * API cleared it) it goes to the login page and remembers where the user was; after a deliberate logout it goes to the
 * plain login page.
 */
export function RequireAuth() {
  const session = useAppSelector(selectSession);
  const signedOutByUser = useAppSelector(selectSignedOutByUser);
  const location = useLocation();

  if (!session) {
    return <Navigate to={signedOutByUser ? LOGIN_PATH : loginLocation(location)} replace />;
  }

  return <Outlet />;
}
