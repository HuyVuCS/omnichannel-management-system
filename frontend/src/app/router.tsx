import { lazy, Suspense } from 'react';
import { createBrowserRouter, type RouteObject } from 'react-router';
import { RequireAuth } from '@/features/auth/RequireAuth';
import { RequireRole } from '@/features/auth/RequireRole';
import { LOGIN_PATH } from '@/features/auth/returnUrl';
import { ComingSoon } from '@/shared/components/ComingSoon';
import { PageLoading } from '@/shared/components/PageLoading';
import { AppLayout } from './AppLayout';
import { navigationItems } from './navigation';

// Route-level code splitting: each page is its own chunk, loaded when its route is first opened. The shell (layout,
// guards, providers) stays in the main chunk. Inside the layout the Suspense boundary is in AppLayout.
const LoginPage = lazy(() => import('@/features/auth/LoginPage').then((m) => ({ default: m.LoginPage })));
const DashboardPage = lazy(() => import('@/pages/DashboardPage').then((m) => ({ default: m.DashboardPage })));
const NotFoundPage = lazy(() => import('@/pages/NotFoundPage').then((m) => ({ default: m.NotFoundPage })));
const ProductsPage = lazy(() => import('@/features/products/ProductsPage').then((m) => ({ default: m.ProductsPage })));
const CustomersPage = lazy(() =>
  import('@/features/customers/CustomersPage').then((m) => ({ default: m.CustomersPage })),
);
const ChannelsPage = lazy(() => import('@/features/channels/ChannelsPage').then((m) => ({ default: m.ChannelsPage })));
const UsersPage = lazy(() => import('@/features/users/UsersPage').then((m) => ({ default: m.UsersPage })));

/** Pages that exist; every other menu item renders a placeholder until its task builds it. */
const builtPages: Record<string, RouteObject['element']> = {
  '/products': <ProductsPage />,
  '/customers': <CustomersPage />,
  '/channels': <ChannelsPage />,
  '/users': <UsersPage />,
};

/**
 * One route per menu item, from the same list as the menu: the page if it is built, otherwise a localised
 * placeholder; wrapped in RequireRole when the item is restricted to roles (same roles as the menu).
 */
const pageRoutes: RouteObject[] = navigationItems
  .filter((item) => item.path !== '/')
  .map((item) => {
    const page = builtPages[item.path] ?? <ComingSoon titleKey={item.labelKey} task={item.plannedTask ?? ''} />;
    return {
      path: item.path,
      element: item.roles ? <RequireRole roles={item.roles}>{page}</RequireRole> : page,
    };
  });

/** Route table (react-router in library mode). Everything except the login page needs a session. */
export const routes: RouteObject[] = [
  {
    path: LOGIN_PATH,
    element: (
      <Suspense fallback={<PageLoading fullScreen />}>
        <LoginPage />
      </Suspense>
    ),
  },
  {
    path: '/',
    element: <RequireAuth />,
    children: [
      {
        element: <AppLayout />,
        children: [
          { index: true, element: <DashboardPage /> },
          ...pageRoutes,
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
