import { http, HttpResponse } from 'msw';
import type { Alert } from '@/features/alerts/alertsApi';
import type { PagedResult } from '@/shared/api/types';
import { COFFEE_ID, TEA_ID } from './catalog';
import { POS_ID } from './dashboard';
import { ORDER_ID, TASK_ID } from './orders';
import { SYNC_TASK_ID } from './processing';
import { api, problem, server } from './server';

export const LOW_STOCK_ALERT_ID = '0199a1b2-0000-7000-8000-0000000000e1';
export const INSUFFICIENT_ALERT_ID = '0199a1b2-0000-7000-8000-0000000000e2';
export const SYNC_ALERT_ID = '0199a1b2-0000-7000-8000-0000000000e3';
export const PROCESSING_ALERT_ID = '0199a1b2-0000-7000-8000-0000000000e4';

/** An item of `GET /api/alerts`: by default nothing but what every alert has. */
export function alert(overrides: Partial<Alert> & Pick<Alert, 'alertId' | 'type'>): Alert {
  return {
    status: 'Open',
    productId: null,
    productSku: null,
    productName: null,
    channelId: null,
    channelName: null,
    taskId: null,
    orderId: null,
    errorCode: null,
    firstOccurredAt: new Date(2026, 9, 3, 8, 5).toISOString(),
    lastOccurredAt: new Date(2026, 9, 3, 8, 5).toISOString(),
    occurrenceCount: 1,
    acknowledgedAt: null,
    acknowledgedByEmail: null,
    resolvedAt: null,
    availableStock: null,
    lowStockThreshold: null,
    ...overrides,
  };
}

/** Tea is low: 3 available at a threshold of 5 - the product's values now. */
export const lowStockAlert = alert({
  alertId: LOW_STOCK_ALERT_ID,
  type: 'LowStock',
  productId: TEA_ID,
  productSku: 'TEA-01',
  productName: 'Trà xanh',
  availableStock: 3,
  lowStockThreshold: 5,
  firstOccurredAt: new Date(2026, 9, 3, 9, 30).toISOString(),
  lastOccurredAt: new Date(2026, 9, 3, 9, 30).toISOString(),
});

/** An order was rejected because coffee was short; it happened three times and a manager has seen it. */
export const insufficientAlert = alert({
  alertId: INSUFFICIENT_ALERT_ID,
  type: 'InsufficientStock',
  status: 'Acknowledged',
  productId: COFFEE_ID,
  productSku: 'COF-01',
  productName: 'Cà phê',
  orderId: ORDER_ID,
  firstOccurredAt: new Date(2026, 9, 2, 14, 0).toISOString(),
  lastOccurredAt: new Date(2026, 9, 3, 9, 0).toISOString(),
  occurrenceCount: 3,
  acknowledgedAt: new Date(2026, 9, 3, 9, 10).toISOString(),
  acknowledgedByEmail: 'manager@example.com',
});

/** The POS channel rejected the stock of tea. */
export const syncFailedAlert = alert({
  alertId: SYNC_ALERT_ID,
  type: 'StockSyncFailed',
  productId: TEA_ID,
  productSku: 'TEA-01',
  productName: 'Trà xanh',
  channelId: POS_ID,
  channelName: 'POS',
  taskId: SYNC_TASK_ID,
  errorCode: 'channel_rejected',
  firstOccurredAt: new Date(2026, 9, 2, 9, 30).toISOString(),
  lastOccurredAt: new Date(2026, 9, 2, 9, 30).toISOString(),
});

/** An order task failed for good and has succeeded since: resolved by the system. */
export const processingFailedAlert = alert({
  alertId: PROCESSING_ALERT_ID,
  type: 'ProcessingFailed',
  status: 'Resolved',
  taskId: TASK_ID,
  orderId: ORDER_ID,
  errorCode: 'db_constraint',
  firstOccurredAt: new Date(2026, 9, 1, 8, 5).toISOString(),
  lastOccurredAt: new Date(2026, 9, 1, 8, 5).toISOString(),
  resolvedAt: new Date(2026, 9, 1, 10, 15).toISOString(),
});

/** One list request: its single-value parameters, and `status` / `type` as the lists the API binds. */
export interface AlertListRequest {
  params: Record<string, string>;
  status: string[];
  type: string[];
}

interface AlertBackend {
  items: Alert[];
  totalCount: number;
  /** Replaces the answer of the list. */
  answer?: () => Response;
  /** Replaces the answer of an acknowledge (the default acknowledges the alert, as the API does). */
  acknowledgeAnswer?: (alertId: string) => Response;
}

/**
 * `GET /api/alerts` (records every request) and `POST /api/alerts/{id}/acknowledge` (records the ids). The default
 * acknowledge changes the alert in `state.items`, so the list read after it shows `Acknowledged`.
 */
export function serveAlerts(initial: Alert[], totalCount = initial.length) {
  const state: AlertBackend = { items: initial, totalCount };
  const requests: AlertListRequest[] = [];
  const acknowledges: string[] = [];

  server.use(
    http.get(api('/alerts'), ({ request }) => {
      const url = new URL(request.url);
      const params = Object.fromEntries([...url.searchParams].filter(([key]) => key !== 'status' && key !== 'type'));
      requests.push({ params, status: url.searchParams.getAll('status'), type: url.searchParams.getAll('type') });
      if (state.answer) {
        return state.answer();
      }

      const page: PagedResult<Alert> = {
        items: state.items,
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 20),
        totalCount: state.totalCount,
      };
      return HttpResponse.json(page);
    }),
    http.post(api('/alerts/:id/acknowledge'), ({ params }) => {
      const alertId = String(params.id);
      acknowledges.push(alertId);
      if (state.acknowledgeAnswer) {
        return state.acknowledgeAnswer(alertId);
      }

      const found = state.items.find((item) => item.alertId === alertId);
      if (!found) {
        return problem(404, { errorCode: 'alert_not_found' });
      }

      const acknowledged: Alert = {
        ...found,
        status: 'Acknowledged',
        acknowledgedAt: new Date(2026, 9, 3, 10, 0).toISOString(),
        acknowledgedByEmail: 'manager@example.com',
      };
      state.items = state.items.map((item) => (item.alertId === alertId ? acknowledged : item));
      return HttpResponse.json(acknowledged);
    }),
  );

  return { state, requests, acknowledges };
}
