import { createBrowserRouter, type RouteObject } from 'react-router';
import { LoginPage } from '@/features/auth/LoginPage';
import { RequireAuth } from '@/features/auth/RequireAuth';
import { RequireRole } from '@/features/auth/RequireRole';
import { LOGIN_PATH } from '@/features/auth/returnUrl';
import { UsersPage } from '@/features/users/UsersPage';
import { DashboardPage } from '@/pages/DashboardPage';
import { NotFoundPage } from '@/pages/NotFoundPage';
import { ComingSoon } from '@/shared/components/ComingSoon';
import { AppLayout } from './AppLayout';
import { navigationItems } from './navigation';

/** Pages of the menu that a later task builds: a localised placeholder until then. */
const placeholderRoutes: RouteObject[] = navigationItems
  .filter((item) => item.plannedTask !== undefined)
  .map((item) => ({
    path: item.path,
    element: <ComingSoon titleKey={item.labelKey} task={item.plannedTask ?? ''} />,
  }));

/**
 * Route table (react-router in library mode). Everything except the login page needs a session; role-restricted pages
 * are wrapped in RequireRole with the same roles as their menu item.
 */
export const routes: RouteObject[] = [
  { path: LOGIN_PATH, element: <LoginPage /> },
  {
    path: '/',
    element: <RequireAuth />,
    children: [
      {
        element: <AppLayout />,
        children: [
          { index: true, element: <DashboardPage /> },
          ...placeholderRoutes,
          {
            path: 'users',
            element: (
              <RequireRole roles={['Admin']}>
                <UsersPage />
              </RequireRole>
            ),
          },
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
];

export function createAppRouter() {
  return createBrowserRouter(routes);
}

export type AppRouter = ReturnType<typeof createAppRouter>;
