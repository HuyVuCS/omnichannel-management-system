import { http, HttpResponse } from 'msw';
import type { SystemEvent } from '@/features/logs/systemEventsApi';
import type { PagedResult } from '@/shared/api/types';
import { TEA_ID } from './catalog';
import { ORDER_ID, TASK_ID } from './orders';
import { SYNC_TASK_ID } from './processing';
import { api, server } from './server';

/** An item of `GET /api/system-events`: by default nothing but what every event has. */
export function systemEvent(overrides: Partial<SystemEvent> & Pick<SystemEvent, 'logId' | 'eventType'>): SystemEvent {
  return {
    level: 'Information',
    createdAt: new Date(2026, 9, 3, 8, 5).toISOString(),
    correlationId: null,
    taskId: null,
    orderId: null,
    workerName: null,
    details: null,
    actorEmail: null,
    targetEmail: null,
    ...overrides,
  };
}

const USER_CHANGE = { targetUserId: 'user-new', actorUserId: 'user-admin' };
const BY_ADMIN = { actorEmail: 'admin@example.com', targetEmail: 'new.user@example.com' };

/** A manager applied a goods-receipt document with two lines. */
export const importedEvent = systemEvent({
  logId: 114,
  eventType: 'inventory.imported',
  createdAt: new Date(2026, 9, 3, 9, 30).toISOString(),
  correlationId: 'corr-import-1',
  details: { referenceId: 'PN/2026-0042', itemCount: 2, totalQuantity: 1500 },
});

/** An order was stored as rejected: one product was short. */
export const insufficientEvent = systemEvent({
  logId: 113,
  level: 'Warning',
  eventType: 'order.insufficient_stock',
  createdAt: new Date(2026, 9, 3, 9, 20).toISOString(),
  correlationId: 'corr-order-7',
  taskId: TASK_ID,
  orderId: ORDER_ID,
  workerName: 'OrderWorker',
  details: { orderId: ORDER_ID, taskId: TASK_ID, items: [{ productId: TEA_ID, requested: 100, available: 4 }] },
});

export const alreadyExistsEvent = systemEvent({
  logId: 112,
  level: 'Warning',
  eventType: 'order.already_exists',
  createdAt: new Date(2026, 9, 3, 9, 10).toISOString(),
  correlationId: 'corr-order-7',
  taskId: TASK_ID,
  orderId: ORDER_ID,
  workerName: 'OrderWorker',
  details: { taskId: TASK_ID, orderId: ORDER_ID, externalOrderId: 'WEB-20261003-AB12CD' },
});

/** A message nobody could process: it names no order and no task. */
export const orderRejectedEvent = systemEvent({
  logId: 111,
  level: 'Error',
  eventType: 'order.message_rejected',
  createdAt: new Date(2026, 9, 3, 9, 0).toISOString(),
  correlationId: 'corr-poison-1',
  workerName: 'OrderWorker',
  details: { messageId: 'msg-17', reasonCode: 'invalid_json' },
});

export const syncRejectedEvent = systemEvent({
  logId: 110,
  level: 'Error',
  eventType: 'stock_sync.message_rejected',
  createdAt: new Date(2026, 9, 3, 8, 50).toISOString(),
  workerName: 'StockSyncWorker',
  details: { messageId: 'msg-18', reasonCode: 'task_of_other_channel', queue: 'stock.sync.pos' },
});

/** The POS channel rejected a stock sync for good, on the third attempt. */
export const processingFailedEvent = systemEvent({
  logId: 109,
  level: 'Error',
  eventType: 'processing.failed',
  createdAt: new Date(2026, 9, 3, 8, 40).toISOString(),
  correlationId: 'corr-sync-7',
  taskId: SYNC_TASK_ID,
  workerName: 'StockSyncWorker',
  details: {
    taskId: SYNC_TASK_ID,
    taskType: 'StockSync',
    attemptNo: 3,
    errorCode: 'channel_rejected',
    errorMessage: 'The channel rejected the product.',
    queue: 'stock.sync.pos',
  },
});

export const retryRequestedEvent = systemEvent({
  logId: 108,
  eventType: 'processing.retry_requested',
  createdAt: new Date(2026, 9, 3, 8, 30).toISOString(),
  correlationId: 'corr-sync-7',
  taskId: SYNC_TASK_ID,
  details: {
    taskId: SYNC_TASK_ID,
    taskType: 'StockSync',
    requestedBy: 'user-manager',
    previousMessageId: 'msg-19',
    newMessageId: 'msg-20',
    attemptBudgetStart: 3,
  },
  actorEmail: 'manager@example.com',
});

export const publishFailedEvent = systemEvent({
  logId: 107,
  level: 'Error',
  eventType: 'outbox.publish_failed',
  createdAt: new Date(2026, 9, 3, 8, 20).toISOString(),
  correlationId: 'corr-order-9',
  taskId: TASK_ID,
  workerName: 'OutboxPublisher',
  details: { outboxEventId: 'outbox-1', eventType: 'order.created', publishAttempts: 10 },
});

export const acknowledgedEvent = systemEvent({
  logId: 106,
  eventType: 'alert.acknowledged',
  createdAt: new Date(2026, 9, 3, 8, 10).toISOString(),
  correlationId: 'corr-ack-1',
  details: { alertId: 'alert-1', type: 'LowStock', acknowledgedBy: 'user-manager' },
  actorEmail: 'manager@example.com',
});

export const userCreatedEvent = systemEvent({
  logId: 105,
  eventType: 'user.created',
  createdAt: new Date(2026, 9, 3, 8, 0).toISOString(),
  correlationId: 'corr-user-1',
  details: { ...USER_CHANGE, role: 'Manager' },
  ...BY_ADMIN,
});

export const roleChangedEvent = systemEvent({
  logId: 104,
  eventType: 'user.role_changed',
  createdAt: new Date(2026, 9, 3, 7, 50).toISOString(),
  correlationId: 'corr-user-2',
  details: { ...USER_CHANGE, from: 'Staff', to: 'Manager' },
  ...BY_ADMIN,
});

export const userLockedEvent = systemEvent({
  logId: 103,
  eventType: 'user.locked',
  createdAt: new Date(2026, 9, 3, 7, 40).toISOString(),
  correlationId: 'corr-user-3',
  details: USER_CHANGE,
  ...BY_ADMIN,
});

export const userUnlockedEvent = systemEvent({
  logId: 102,
  eventType: 'user.unlocked',
  createdAt: new Date(2026, 9, 3, 7, 30).toISOString(),
  correlationId: 'corr-user-4',
  details: USER_CHANGE,
  ...BY_ADMIN,
});

export const passwordResetEvent = systemEvent({
  logId: 101,
  eventType: 'user.password_reset',
  createdAt: new Date(2026, 9, 3, 7, 20).toISOString(),
  correlationId: 'corr-user-5',
  details: USER_CHANGE,
  ...BY_ADMIN,
});

/** A Manager asked for an export (audit: bulk data leaves the system). */
export const exportRequestedEvent = systemEvent({
  logId: 120,
  eventType: 'report.export_requested',
  createdAt: new Date(2026, 9, 5, 10, 30).toISOString(),
  correlationId: 'corr-export-1',
  taskId: '0199a1b2-0000-7000-8000-0000000000e1',
  details: { reportType: 'Orders', requestedBy: 'user-manager' },
  actorEmail: 'manager@example.com',
});

/** A message of the export queue that could not be processed. */
export const exportRejectedEvent = systemEvent({
  logId: 121,
  level: 'Error',
  eventType: 'report_export.message_rejected',
  createdAt: new Date(2026, 9, 5, 10, 31).toISOString(),
  correlationId: 'corr-export-2',
  workerName: 'ReportExportWorker',
  details: { messageId: 'msg-41', reasonCode: 'unknown_task' },
});

const RECONCILIATION_CORRELATION_ID = 'reconciliation-0199b3c27d1e7c4a9f00aabbccddeeff';

/** A reconciliation run that found drift on every channel it could read. */
export const reconciliationEvent = systemEvent({
  logId: 131,
  level: 'Warning',
  eventType: 'reconciliation.completed',
  createdAt: new Date(2026, 9, 5, 11, 15).toISOString(),
  correlationId: RECONCILIATION_CORRELATION_ID,
  workerName: 'ReconciliationWorker',
  details: {
    runId: '0199b3c2-7d1e-7c4a-9f00-aabbccddeeff',
    checked: 1340,
    inSync: 1328,
    drifted: 12,
    tasksCreated: 12,
    deferred: 0,
    failedRetried: 0,
    skippedUnfinished: 3,
    skippedRejected: 1,
    unreadable: [],
  },
});

/**
 * A run that reached its cap, repeated three failed syncs of products that do not differ, and could not read two
 * channels: one with a code this client knows, one without.
 */
export const reconciliationWithTroubleEvent = systemEvent({
  ...reconciliationEvent,
  logId: 132,
  details: {
    ...reconciliationEvent.details,
    drifted: 620,
    tasksCreated: 500,
    deferred: 123,
    failedRetried: 3,
    unreadable: [
      { channel: 'POS', errorCode: 'channel_unavailable', pairs: 40 },
      { channel: 'Website', errorCode: 'tls_handshake_failed', pairs: 7 },
    ],
  },
});

/** One event per type the API has an allowlist entry for, newest first; the last five are the Admin's. */
/** A run of the stale-task sweep that failed two tasks nothing had touched for half an hour. */
export const staleTasksFailedEvent = systemEvent({
  logId: 151,
  level: 'Warning',
  eventType: 'processing.stale_tasks_failed',
  createdAt: new Date(2026, 9, 13, 16, 5).toISOString(),
  correlationId: 'stale-tasks-0199c3c27d1e7c4a9f00aabbccddee01',
  workerName: 'StaleTaskSweep',
  details: {
    runId: '0199c3c2-7d1e-7c4a-9f00-aabbccddee01',
    staleAfterSeconds: 1800,
    failed: 2,
    taskIds: [SYNC_TASK_ID, '0199a1b2-0000-7000-8000-0000000000cc'],
    notListed: 0,
  },
});

export const everyEventType: SystemEvent[] = [
  importedEvent,
  insufficientEvent,
  alreadyExistsEvent,
  orderRejectedEvent,
  syncRejectedEvent,
  processingFailedEvent,
  retryRequestedEvent,
  publishFailedEvent,
  acknowledgedEvent,
  userCreatedEvent,
  roleChangedEvent,
  userLockedEvent,
  userUnlockedEvent,
  passwordResetEvent,
];

/** What a Manager gets: no user management events. */
export const managerEvents: SystemEvent[] = everyEventType.filter((event) => !event.eventType.startsWith('user.'));

/** A request of the event list as the server saw it: single-value parameters, and the two that can be repeated. */
export interface SystemEventListRequest {
  params: Record<string, string>;
  level: string[];
  eventType: string[];
}

interface SystemEventBackend {
  items: SystemEvent[];
  totalCount: number;
  /** Replaces the answer of the list. */
  answer?: () => Response;
}

/** `GET /api/system-events`: records every request and answers with `state.items` whatever was asked for. */
export function serveSystemEvents(initial: SystemEvent[], totalCount = initial.length) {
  const state: SystemEventBackend = { items: initial, totalCount };
  const requests: SystemEventListRequest[] = [];

  server.use(
    http.get(api('/system-events'), ({ request }) => {
      const url = new URL(request.url);
      const params = Object.fromEntries(
        [...url.searchParams].filter(([key]) => key !== 'level' && key !== 'eventType'),
      );
      requests.push({
        params,
        level: url.searchParams.getAll('level'),
        eventType: url.searchParams.getAll('eventType'),
      });
      if (state.answer) {
        return state.answer();
      }

      const page: PagedResult<SystemEvent> = {
        items: state.items,
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 20),
        totalCount: state.totalCount,
      };
      return HttpResponse.json(page);
    }),
  );

  return { state, requests };
}
