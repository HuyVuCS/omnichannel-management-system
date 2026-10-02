import {
  AppstoreOutlined,
  BarChartOutlined,
  BellOutlined,
  DashboardOutlined,
  FileSearchOutlined,
  FormOutlined,
  InboxOutlined,
  ShopOutlined,
  ShoppingCartOutlined,
  SwapOutlined,
  SyncOutlined,
  TeamOutlined,
  UserOutlined,
} from '@ant-design/icons';
import type { ReactNode } from 'react';
import type { Role } from '@/features/auth/session';

export interface NavigationItem {
  /** Route path; also the menu key. */
  path: string;
  /** Translation key of the menu label and page title. */
  labelKey: string;
  icon: ReactNode;
  /** Only these roles see the item and may open the page; everyone when omitted. */
  roles?: readonly Role[];
  /** Roadmap task that builds the page; until then the route renders a placeholder. */
  plannedTask?: string;
}

/** Every page of the dashboard (architecture doc §3.2), in menu order. The single source for menu and routes. */
export const navigationItems: readonly NavigationItem[] = [
  { path: '/', labelKey: 'nav.dashboard', icon: <DashboardOutlined /> },
  { path: '/order-entry', labelKey: 'nav.orderEntry', icon: <FormOutlined /> },
  { path: '/orders', labelKey: 'nav.orders', icon: <ShoppingCartOutlined /> },
  { path: '/inventory', labelKey: 'nav.inventory', icon: <InboxOutlined /> },
  { path: '/inventory/movements', labelKey: 'nav.inventoryMovements', icon: <SwapOutlined /> },
  { path: '/products', labelKey: 'nav.products', icon: <AppstoreOutlined /> },
  { path: '/customers', labelKey: 'nav.customers', icon: <TeamOutlined /> },
  { path: '/channels', labelKey: 'nav.channels', icon: <ShopOutlined /> },
  { path: '/processing', labelKey: 'nav.processing', icon: <SyncOutlined /> },
  { path: '/alerts', labelKey: 'nav.alerts', icon: <BellOutlined />, plannedTask: '018' },
  { path: '/logs', labelKey: 'nav.logs', icon: <FileSearchOutlined />, plannedTask: '018' },
  { path: '/reports', labelKey: 'nav.reports', icon: <BarChartOutlined />, plannedTask: '021' },
  { path: '/users', labelKey: 'nav.users', icon: <UserOutlined />, roles: ['Admin'] },
];

/** The menu item a location belongs to: the longest item path that is the location or a parent of it. */
export function activeNavigationPath(pathname: string): string | undefined {
  return navigationItems
    .map((item) => item.path)
    .filter((path) => (path === '/' ? pathname === '/' : pathname === path || pathname.startsWith(`${path}/`)))
    .sort((a, b) => b.length - a.length)[0];
}
