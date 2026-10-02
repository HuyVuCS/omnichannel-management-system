import type { OrderStatus } from '@/features/orders/ordersApi';
import type { TaskType } from '@/features/processing/processingApi';
import { baseApi } from '@/shared/api/baseApi';

export interface DashboardOrderStatusCount {
  status: OrderStatus;
  count: number;
}

export interface DashboardChannelOrders {
  channelId: string;
  slug: string;
  name: string;
  total: number;
  reserved: number;
  insufficientStock: number;
}

/** One entry per task type. `pending` / `inProgress` / `retryScheduled` are current; the others happened in the range. */
export interface DashboardProcessing {
  taskType: TaskType;
  pending: number;
  inProgress: number;
  retryScheduled: number;
  failed: number;
  succeeded: number;
}

/** One entry per channel with stock sync. `retryScheduled` is current, `failed` in the range, the last success ever. */
export interface DashboardStockSync {
  channelId: string;
  slug: string;
  name: string;
  retryScheduled: number;
  failed: number;
  lastSucceededAt: string | null;
}

/**
 * `GET /api/dashboard/summary`: the numbers of the overview page in one request. Every number is the server's (SQL
 * aggregates at `generatedAt`); this client adds, compares and judges nothing.
 */
export interface DashboardSummary {
  generatedAt: string;
  from: string;
  to: string;
  /** Orders created in the range. `salesAmount` = Reserved + Completed. */
  orders: {
    total: number;
    byStatus: DashboardOrderStatusCount[];
    byChannel: DashboardChannelOrders[];
    salesAmount: number;
  };
  /** Now. `lowStock` follows the rule of the inventory list's low-stock filter. */
  inventory: { products: number; lowStock: number; outOfStock: number };
  processing: DashboardProcessing[];
  stockSync: DashboardStockSync[];
  /** Now. */
  outbox: { pending: number; failed: number; oldestPendingAt: string | null };
}

/** ISO 8601 instants with offset; both optional (the server's default: the last 24 hours up to now). */
export interface DashboardSummaryArgs {
  from?: string;
  to?: string;
}

export const dashboardApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    getDashboardSummary: build.query<DashboardSummary, DashboardSummaryArgs>({
      query: ({ from, to }) => ({ url: 'dashboard/summary', params: { ...(from && { from }), ...(to && { to }) } }),
      // Realtime hints invalidate this tag (`features/realtime/hintTags.ts`): the page never polls.
      providesTags: ['DashboardSummary'],
    }),
  }),
});

export const { useGetDashboardSummaryQuery } = dashboardApi;
