import { baseApi } from '@/shared/api/baseApi';
import { isApiError, type PagedResult } from '@/shared/api/types';
import type { SortDirection } from '@/shared/hooks/useListQueryState';

/** Values of the API's alert enums (serialised as strings). */
export const ALERT_TYPES = ['LowStock', 'InsufficientStock', 'StockSyncFailed', 'ProcessingFailed'] as const;
export type AlertType = (typeof ALERT_TYPES)[number];

export const ALERT_STATUSES = ['Open', 'Acknowledged', 'Resolved'] as const;
export type AlertStatus = (typeof ALERT_STATUSES)[number];

/**
 * An alert as `GET /api/alerts` returns it (`AlertResponse`). Which fields are set depends on the type; the API sends
 * no error message, only the code.
 */
export interface Alert {
  alertId: string;
  type: AlertType;
  status: AlertStatus;
  /** The product of a `LowStock`, `InsufficientStock` or `StockSyncFailed` alert. */
  productId: string | null;
  productSku: string | null;
  productName: string | null;
  /** The channel of a `StockSyncFailed` alert. */
  channelId: string | null;
  channelName: string | null;
  /** The latest task of a failure alert. */
  taskId: string | null;
  /** The latest order (`InsufficientStock`; `ProcessingFailed` when the task has one). */
  orderId: string | null;
  /** Error code of the latest failure (failure alerts only). */
  errorCode: string | null;
  firstOccurredAt: string;
  lastOccurredAt: string;
  occurrenceCount: number;
  acknowledgedAt: string | null;
  /** Null when nobody acknowledged it or the user is not known any more. */
  acknowledgedByEmail: string | null;
  resolvedAt: string | null;
  /** `LowStock` only: the product's available stock **now**, not when the alert opened. */
  availableStock: number | null;
  /** `LowStock` only: the product's threshold **now**. */
  lowStockThreshold: number | null;
}

/** Sort fields of `GET /api/alerts`. Without a sort: most recent occurrence first; every field is descending unless `asc`. */
export const ALERT_SORT_FIELDS = ['lastOccurredAt', 'firstOccurredAt', 'occurrenceCount'] as const;
export type AlertSortField = (typeof ALERT_SORT_FIELDS)[number];

/**
 * `AlertListQuery`. `status` and `type` are repeated parameters; **without a status the server shows what needs
 * attention (`Open` and `Acknowledged`)** - that default is the server's and is never sent from here. `from`/`to` are
 * ISO 8601 instants (inclusive, on the last occurrence).
 */
export interface AlertListArgs {
  page: number;
  pageSize: number;
  sortBy?: AlertSortField;
  sortDir?: SortDirection;
  status?: readonly AlertStatus[];
  type?: readonly AlertType[];
  productId?: string;
  channelId?: string;
  from?: string;
  to?: string;
}

/** Every alert list; a single alert is tagged with its id. Realtime hints invalidate both (`features/realtime`). */
export const ALERT_LIST_TAG = { type: 'Alert', id: 'LIST' } as const;

/** The API says the alert cannot be acknowledged any more, or is gone: what is on screen about it is stale. */
const ACKNOWLEDGE_STALE_CODES: readonly string[] = ['alert_resolved', 'alert_not_found'];

function listQuery({ status = [], type = [], ...single }: AlertListArgs): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(single)) {
    if (value !== undefined && value !== '') {
      params.set(key, String(value));
    }
  }

  // One parameter per value (`status=Open&status=Resolved`), as the API binds lists.
  for (const value of status) {
    params.append('status', value);
  }

  for (const value of type) {
    params.append('type', value);
  }

  return params.toString();
}

export const alertsApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    listAlerts: build.query<PagedResult<Alert>, AlertListArgs>({
      query: (args) => `alerts?${listQuery(args)}`,
      providesTags: (result) => [
        ALERT_LIST_TAG,
        ...(result?.items ?? []).map((alert) => ({ type: 'Alert' as const, id: alert.alertId })),
      ],
    }),
    /**
     * `Open` → `Acknowledged` (Manager/Admin). On success, and when the API says the alert is resolved already or does
     * not exist, what is on screen about it is stale: the alert, the lists and the summary are fetched again. Any other
     * failure changed nothing.
     */
    acknowledgeAlert: build.mutation<Alert, string>({
      query: (alertId) => ({ url: `alerts/${alertId}/acknowledge`, method: 'POST' }),
      invalidatesTags: (_result, error, alertId) =>
        !error || (isApiError(error) && ACKNOWLEDGE_STALE_CODES.includes(error.problem?.errorCode ?? ''))
          ? [{ type: 'Alert', id: alertId }, ALERT_LIST_TAG, 'DashboardSummary']
          : [],
    }),
  }),
});

export const { useListAlertsQuery, useAcknowledgeAlertMutation } = alertsApi;
