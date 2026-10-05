import { Suspense } from 'react';
import { createBrowserRouter, type RouteObject } from 'react-router';
import { ALERTS_PATH } from '@/features/alerts/paths';
import { RequireAuth } from '@/features/auth/RequireAuth';
import { RequireRole } from '@/features/auth/RequireRole';
import { LOGIN_PATH } from '@/features/auth/returnUrl';
import { CHANNEL_DETAIL_ROUTE, CHANNELS_PATH } from '@/features/channels/paths';
import { LOGS_PATH } from '@/features/logs/paths';
import { INVENTORY_PATH, MOVEMENTS_PATH, STOCK_IMPORT_PATH, STOCK_IMPORT_ROLES } from '@/features/inventory/paths';
import { ORDER_DETAIL_ROUTE, ORDER_ENTRY_PATH, ORDERS_PATH } from '@/features/orders/paths';
import { PROCESSING_PATH, PROCESSING_TASK_ROUTE } from '@/features/processing/paths';
import { REPORTS_PATH } from '@/features/reports/paths';
import { RouteErrorPage } from '@/pages/RouteErrorPage';
import { ComingSoon } from '@/shared/components/ComingSoon';
import { PageLoading } from '@/shared/components/PageLoading';
import { AppLayout } from './AppLayout';
import { lazyPage } from './lazyPage';
import { navigationItems } from './navigation';

// Route-level code splitting: each page is its own chunk, loaded when its route is first opened. The shell (layout,
// guards, providers) stays in the main chunk. Inside the layout the Suspense boundary is in AppLayout. A chunk that
// cannot be loaded ends on the route error page (see `lazyPage`).
const AlertsPage = lazyPage(() => import('@/features/alerts/AlertsPage').then((m) => ({ default: m.AlertsPage })));
const LoginPage = lazyPage(() => import('@/features/auth/LoginPage').then((m) => ({ default: m.LoginPage })));
const DashboardPage = lazyPage(() =>
  import('@/features/dashboard/DashboardPage').then((m) => ({ default: m.DashboardPage })),
);
const LogsPage = lazyPage(() => import('@/features/logs/LogsPage').then((m) => ({ default: m.LogsPage })));
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
const ChannelDetailPage = lazyPage(() =>
  import('@/features/channels/ChannelDetailPage').then((m) => ({ default: m.ChannelDetailPage })),
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
const OrderEntryPage = lazyPage(() =>
  import('@/features/orders/OrderEntryPage').then((m) => ({ default: m.OrderEntryPage })),
);
const OrdersPage = lazyPage(() => import('@/features/orders/OrdersPage').then((m) => ({ default: m.OrdersPage })));
const OrderDetailPage = lazyPage(() =>
  import('@/features/orders/OrderDetailPage').then((m) => ({ default: m.OrderDetailPage })),
);
const ProcessingPage = lazyPage(() =>
  import('@/features/processing/ProcessingPage').then((m) => ({ default: m.ProcessingPage })),
);
const ProcessingTaskPage = lazyPage(() =>
  import('@/features/processing/ProcessingTaskPage').then((m) => ({ default: m.ProcessingTaskPage })),
);
const ReportsPage = lazyPage(() => import('@/features/reports/ReportsPage').then((m) => ({ default: m.ReportsPage })));

/** Pages that exist; every other menu item renders a placeholder until its task builds it. */
const builtPages: Record<string, RouteObject['element']> = {
  [ORDER_ENTRY_PATH]: <OrderEntryPage />,
  [ORDERS_PATH]: <OrdersPage />,
  [INVENTORY_PATH]: <InventoryPage />,
  [MOVEMENTS_PATH]: <MovementsPage />,
  '/products': <ProductsPage />,
  '/customers': <CustomersPage />,
  [CHANNELS_PATH]: <ChannelsPage />,
  [PROCESSING_PATH]: <ProcessingPage />,
  [ALERTS_PATH]: <AlertsPage />,
  [LOGS_PATH]: <LogsPage />,
  [REPORTS_PATH]: <ReportsPage />,
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
  { path: ORDER_DETAIL_ROUTE, element: <OrderDetailPage /> },
  { path: CHANNEL_DETAIL_ROUTE, element: <ChannelDetailPage /> },
  { path: PROCESSING_TASK_ROUTE, element: <ProcessingTaskPage /> },
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
