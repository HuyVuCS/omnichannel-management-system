import type { ProcessingAttempt, TaskStatus, TaskType } from '@/features/processing/processingApi';
import { baseApi } from '@/shared/api/baseApi';
import type { PagedResult } from '@/shared/api/types';
import type { SortDirection } from '@/shared/hooks/useListQueryState';

/** Values of the API's `OrderStatus` enum (serialised as strings). */
export const ORDER_STATUSES = ['Pending', 'Reserved', 'InsufficientStock', 'Cancelled', 'Completed'] as const;
export type OrderStatus = (typeof ORDER_STATUSES)[number];

export interface OrderChannel {
  channelId: string;
  slug: string;
  name: string;
}

export interface OrderCustomer {
  customerId: string;
  name: string;
}

export interface OrderSummary {
  orderId: string;
  externalOrderId: string;
  status: OrderStatus;
  totalAmount: number;
  /** Number of order lines. */
  itemCount: number;
  channel: OrderChannel;
  customer: OrderCustomer;
  createdAt: string;
}

export interface OrderItem {
  productId: string;
  sku: string;
  productName: string;
  /** Ordered quantity. */
  stock: number;
  /** Price snapshot taken at intake. */
  unitPrice: number;
  lineTotal: number;
}

/** A processing task tied to the order, with all its attempts (ordered by number). */
export interface OrderTask {
  taskId: string;
  taskType: TaskType;
  status: TaskStatus;
  retryCount: number;
  correlationId: string;
  messageId: string | null;
  createdAt: string;
  updatedAt: string | null;
  attempts: ProcessingAttempt[];
}

export interface OrderDetail extends OrderSummary {
  items: OrderItem[];
  tasks: OrderTask[];
}

/** Sort fields of `GET /api/orders`. Without a sort the API answers newest first (`createdAt`, descending). */
export const ORDER_SORT_FIELDS = ['createdAt', 'externalOrderId', 'totalAmount'] as const;
export type OrderSortField = (typeof ORDER_SORT_FIELDS)[number];

/** `from`/`to` are ISO 8601 instants (inclusive, on the creation time); `channel` is a channel slug. */
export interface OrderListArgs {
  page: number;
  pageSize: number;
  search?: string;
  sortBy?: OrderSortField;
  sortDir?: SortDirection;
  channel?: string;
  status?: OrderStatus;
  customerId?: string;
  from?: string;
  to?: string;
}

/** Body of `POST /api/orders/{slug}`. `externalOrderId` is the idempotency key of the order on its channel. */
export interface SubmitOrderRequest {
  externalOrderId: string;
  customerId: string;
  items: { productId: string; stock: number }[];
}

/**
 * 202: the order was accepted and is processed in the background by the Order Worker; `taskId` is how to follow it.
 * `duplicate` = this channel had already accepted the same order (same content): nothing new was started and the task is
 * the one of the first submission.
 */
export interface SubmitOrderResponse {
  taskId: string;
  status: TaskStatus;
  correlationId: string;
  duplicate: boolean;
}

/** Every order list; a single order is tagged with its id. */
export const ORDER_LIST_TAG = { type: 'Order', id: 'LIST' } as const;

function compact(params: Record<string, string | number | undefined>) {
  return Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined && value !== ''));
}

export const ordersApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    listOrders: build.query<PagedResult<OrderSummary>, OrderListArgs>({
      query: (args) => ({ url: 'orders', params: compact({ ...args }) }),
      providesTags: [ORDER_LIST_TAG],
    }),
    getOrder: build.query<OrderDetail, string>({
      query: (orderId) => `orders/${orderId}`,
      providesTags: (_result, _error, orderId) => [{ type: 'Order', id: orderId }],
    }),
    /**
     * Invalidates nothing: a 202 has not changed any list yet. The order entry page follows the task and invalidates
     * the lists when the task is finished.
     */
    submitOrder: build.mutation<SubmitOrderResponse, { slug: string; order: SubmitOrderRequest }>({
      query: ({ slug, order }) => ({ url: `orders/${encodeURIComponent(slug)}`, method: 'POST', body: order }),
    }),
  }),
});

export const { useListOrdersQuery, useGetOrderQuery, useSubmitOrderMutation } = ordersApi;
