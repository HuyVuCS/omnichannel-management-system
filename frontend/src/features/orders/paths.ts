export const ORDER_ENTRY_PATH = '/order-entry';
export const ORDERS_PATH = '/orders';

/**
 * The API lets Manager and Admin record the payment of an order; everyone signed in may read it. Kept next to the
 * paths, like the roles of a manual retry.
 */
export const PAYMENT_ROLES = ['Admin', 'Manager'] as const;

/** Route pattern of the order detail page. */
export const ORDER_DETAIL_ROUTE = `${ORDERS_PATH}/:orderId`;

/** The detail page of one order. */
export function orderPath(orderId: string): string {
  return `${ORDERS_PATH}/${encodeURIComponent(orderId)}`;
}

/**
 * Router state of a link from the order list to a detail page: the list's address (path + query), so "Back" returns
 * to the same page, sort and filters.
 */
export interface OrderDetailLocationState {
  from: string;
}

/** The list address a detail page was opened from, when there is one and it really is an address of the order list. */
export function orderListAddress(state: unknown): string | undefined {
  const from = (state as Partial<OrderDetailLocationState> | null)?.from;
  return typeof from === 'string' && (from === ORDERS_PATH || from.startsWith(`${ORDERS_PATH}?`)) ? from : undefined;
}
