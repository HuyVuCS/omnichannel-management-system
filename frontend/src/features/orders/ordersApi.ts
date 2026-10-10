import {
  PROCESSING_LIST_TAG,
  type ProcessingAttempt,
  type TaskStatus,
  type TaskType,
} from '@/features/processing/processingApi';
import { baseApi } from '@/shared/api/baseApi';
import { isApiError, isUnexpectedError, type PagedResult } from '@/shared/api/types';
import type { SortDirection } from '@/shared/hooks/useListQueryState';

/** Values of the API's `OrderStatus` enum (serialised as strings). */
export const ORDER_STATUSES = ['Pending', 'Reserved', 'InsufficientStock', 'Cancelled', 'Completed'] as const;
export type OrderStatus = (typeof ORDER_STATUSES)[number];

/** Values of the API's `PaymentStatus` enum. Independent of the order status: a paid order keeps its status. */
export const PAYMENT_STATUSES = ['Unpaid', 'Paid'] as const;
export type PaymentStatus = (typeof PAYMENT_STATUSES)[number];

/** Values of the API's `PaymentMethod` enum. */
export const PAYMENT_METHODS = ['Cash', 'BankTransfer', 'Card', 'EWallet'] as const;
export type PaymentMethod = (typeof PAYMENT_METHODS)[number];

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
  paymentStatus: PaymentStatus;
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

/** The payment of an order. Everything but `status` is null while the order is `Unpaid`. */
export interface OrderPayment {
  status: PaymentStatus;
  paidAt: string | null;
  /** Who recorded the payment; null when that user is gone. */
  paidByEmail: string | null;
  method: PaymentMethod | null;
  /** The receipt or transfer number that was entered; null when none was. */
  reference: string | null;
}

export interface OrderInvoice {
  number: string;
  issuedAt: string;
}

export interface OrderDetail extends OrderSummary {
  items: OrderItem[];
  tasks: OrderTask[];
  payment: OrderPayment;
  /** Null until the invoice task of the order has succeeded. */
  invoice: OrderInvoice | null;
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

/** Body of `POST /api/orders/{orderId}/payment`. The reference is optional: a blank one is not sent. */
export interface PayOrderRequest {
  paymentMethod: PaymentMethod;
  paymentReference?: string;
}

/**
 * 200: the payment is recorded and its follow-up work is queued - the invoice always, the confirmation e-mail only
 * when the customer has an e-mail address (`emailTaskId` is null otherwise).
 */
export interface PayOrderResponse {
  orderId: string;
  paymentStatus: PaymentStatus;
  paidAt: string;
  invoiceTaskId: string;
  emailTaskId: string | null;
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
    /**
     * Records the payment of an order (Manager/Admin). Whatever the API answers about the order - paid, "already
     * paid", "cannot be paid" - what is on screen about it is stale, so the order, the order lists, the task lists (two
     * new tasks) and the summary (it counts them) are fetched again. Only a network error or a 5xx changes nothing.
     */
    payOrder: build.mutation<PayOrderResponse, { orderId: string; payment: PayOrderRequest }>({
      query: ({ orderId, payment }) => {
        const paymentReference = payment.paymentReference?.trim();
        return {
          url: `orders/${orderId}/payment`,
          method: 'POST',
          body: { paymentMethod: payment.paymentMethod, ...(paymentReference ? { paymentReference } : {}) },
        };
      },
      invalidatesTags: (_result, error, { orderId }) =>
        isApiError(error) && isUnexpectedError(error)
          ? []
          : [{ type: 'Order', id: orderId }, ORDER_LIST_TAG, PROCESSING_LIST_TAG, 'DashboardSummary'],
    }),
  }),
});

export const { useListOrdersQuery, useGetOrderQuery, useSubmitOrderMutation, usePayOrderMutation } = ordersApi;
