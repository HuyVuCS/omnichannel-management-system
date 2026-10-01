import { Suspense } from 'react';
import { createBrowserRouter, type RouteObject } from 'react-router';
import { RequireAuth } from '@/features/auth/RequireAuth';
import { RequireRole } from '@/features/auth/RequireRole';
import { LOGIN_PATH } from '@/features/auth/returnUrl';
import { INVENTORY_PATH, MOVEMENTS_PATH, STOCK_IMPORT_PATH, STOCK_IMPORT_ROLES } from '@/features/inventory/paths';
import { RouteErrorPage } from '@/pages/RouteErrorPage';
import { ComingSoon } from '@/shared/components/ComingSoon';
import { PageLoading } from '@/shared/components/PageLoading';
import { AppLayout } from './AppLayout';
import { lazyPage } from './lazyPage';
import { navigationItems } from './navigation';

// Route-level code splitting: each page is its own chunk, loaded when its route is first opened. The shell (layout,
// guards, providers) stays in the main chunk. Inside the layout the Suspense boundary is in AppLayout. A chunk that
// cannot be loaded ends on the route error page (see `lazyPage`).
const LoginPage = lazyPage(() => import('@/features/auth/LoginPage').then((m) => ({ default: m.LoginPage })));
const DashboardPage = lazyPage(() => import('@/pages/DashboardPage').then((m) => ({ default: m.DashboardPage })));
const NotFoundPage = lazyPage(() => import('@/pages/NotFoundPage').then((m) => ({ default: m.NotFoundPage })));
const ProductsPage = lazyPage(() =>
  import('@/features/products/ProductsPage').then((m) => ({ default: m.ProductsPage })),
);
const CustomersPage = lazyPage(() =>
  import('@/features/customers/CustomersPage').then((m) => ({ default: m.CustomersPage })),
);
const ChannelsPage = lazyPage(() =>
  import('@/features/channels/ChannelsPage').then((m) => ({ default: m.ChannelsPage })),
);
const UsersPage = lazyPage(() => import('@/features/users/UsersPage').then((m) => ({ default: m.UsersPage })));
const InventoryPage = lazyPage(() =>
  import('@/features/inventory/InventoryPage').then((m) => ({ default: m.InventoryPage })),
);
const MovementsPage = lazyPage(() =>
  import('@/features/inventory/MovementsPage').then((m) => ({ default: m.MovementsPage })),
);
const StockImportPage = lazyPage(() =>
  import('@/features/inventory/StockImportPage').then((m) => ({ default: m.StockImportPage })),
);

/** Pages that exist; every other menu item renders a placeholder until its task builds it. */
const builtPages: Record<string, RouteObject['element']> = {
  [INVENTORY_PATH]: <InventoryPage />,
  [MOVEMENTS_PATH]: <MovementsPage />,
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

/**
 * Pages without a menu item: reached from another page. The menu highlights the item whose path is their parent
 * (`activeNavigationPath`).
 */
const pagesOutsideMenu: RouteObject[] = [
  {
    path: STOCK_IMPORT_PATH,
    element: (
      <RequireRole roles={STOCK_IMPORT_ROLES}>
        <StockImportPage />
      </RequireRole>
    ),
  },
];

/**
 * Route table (react-router in library mode). Everything except the login page needs a session.
 *
 * Errors: a page that cannot be loaded or that throws while rendering is replaced by the route error page inside the
 * layout (the pathless route below), so the menu stays usable. The login page and the shell itself have nothing around
 * them and use the full-screen variant.
 */
export const routes: RouteObject[] = [
  {
    path: LOGIN_PATH,
    element: (
      <Suspense fallback={<PageLoading fullScreen />}>
        <LoginPage />
      </Suspense>
    ),
    errorElement: <RouteErrorPage fullScreen />,
  },
  {
    path: '/',
    element: <RequireAuth />,
    errorElement: <RouteErrorPage fullScreen />,
    children: [
      {
        element: <AppLayout />,
        children: [
          {
            errorElement: <RouteErrorPage />,
            children: [
              { index: true, element: <DashboardPage /> },
              ...pageRoutes,
              ...pagesOutsideMenu,
              { path: '*', element: <NotFoundPage /> },
            ],
          },
        ],
      },
    ],
  },
];

export function createAppRouter() {
  return createBrowserRouter(routes);
}

export type AppRouter = ReturnType<typeof createAppRouter>;
