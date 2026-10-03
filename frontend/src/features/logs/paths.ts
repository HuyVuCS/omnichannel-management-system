export const LOGS_PATH = '/logs';

/** The API lets Manager and Admin read the events; kept next to the paths, like the roles of the other restricted pages. */
export const LOGS_ROLES = ['Admin', 'Manager'] as const;

/** User management events (`user.*`) are returned to an Admin only: only an Admin is offered them as a filter. */
export const USER_EVENT_ROLES = ['Admin'] as const;

/** Address parameters of the filters that take several values: one entry per value, as the API reads them. */
export const LEVEL_PARAM = 'level';
export const EVENT_TYPE_PARAM = 'eventType';

export const CORRELATION_PARAM = 'correlationId';
export const ORDER_PARAM = 'orderId';
export const TASK_PARAM = 'taskId';

/** Start of the "last 24 hours" window, an instant (see `range.ts`). */
export const SINCE_PARAM = 'since';

/** Filters of the page in the address, next to `page` / `pageSize` / `sortDir` and the time window. */
export const FILTER_PARAMS = [LEVEL_PARAM, EVENT_TYPE_PARAM, CORRELATION_PARAM, ORDER_PARAM, TASK_PARAM] as const;

/**
 * The trace view (architecture doc §5.3): every event of one flow. The address of the list as it is now - same time
 * window, sort direction and page size - with the correlation id as its only filter, on the first page.
 */
export function tracePath(current: URLSearchParams, correlationId: string): string {
  const params = new URLSearchParams(current);
  for (const name of [...FILTER_PARAMS, 'page']) {
    params.delete(name);
  }

  params.set(CORRELATION_PARAM, correlationId);
  return `${LOGS_PATH}?${params.toString()}`;
}
