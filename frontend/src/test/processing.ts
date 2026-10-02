import { http, HttpResponse } from 'msw';
import type { ProcessingTaskDetail, ProcessingTaskSummary } from '@/features/processing/processingApi';
import type { PagedResult } from '@/shared/api/types';
import { POS_ID, WEBSITE_ID } from './dashboard';
import { attempt, channel, ORDER_ID, task, TASK_ID } from './orders';
import { api, problem, server } from './server';

export const SYNC_TASK_ID = '0199a1b2-0000-7000-8000-0000000000bb';
export const PRODUCT_ID = '11111111-1111-4111-8111-111111111111';
/** A channel the channel list does not contain. */
export const UNKNOWN_CHANNEL_ID = '0199a1b2-0000-7000-8000-00000000c999';

/** The channels a task can name, with the ids the task fixtures use. */
export const taskChannels = [
  channel({ channelId: WEBSITE_ID, slug: 'website', name: 'Website', type: 'Website' }),
  channel({ channelId: POS_ID, slug: 'pos', name: 'POS', type: 'Pos' }),
];

/** A row of `GET /api/processing/tasks`: by default a succeeded order task with one attempt. */
export function taskSummary(overrides: Partial<ProcessingTaskSummary> = {}): ProcessingTaskSummary {
  return {
    taskId: TASK_ID,
    taskType: 'OrderProcessing',
    status: 'Succeeded',
    orderId: ORDER_ID,
    channelId: null,
    productId: null,
    productSku: null,
    retryCount: 0,
    attemptBudgetStart: 0,
    correlationId: 'corr-task-1',
    messageId: 'message-1',
    createdAt: new Date(2026, 9, 1, 8, 5).toISOString(),
    updatedAt: new Date(2026, 9, 1, 8, 5, 2).toISOString(),
    attemptCount: 1,
    lastAttempt: attempt(),
    ...overrides,
  };
}

/** A stock-sync task of the POS channel that the channel rejected. */
export const failedSync = taskSummary({
  taskId: SYNC_TASK_ID,
  taskType: 'StockSync',
  status: 'Failed',
  orderId: null,
  channelId: POS_ID,
  productId: PRODUCT_ID,
  productSku: 'TEA-01',
  correlationId: 'corr-sync-7',
  createdAt: new Date(2026, 9, 2, 9, 30).toISOString(),
  updatedAt: new Date(2026, 9, 2, 9, 30, 4).toISOString(),
  attemptCount: 2,
  lastAttempt: attempt({
    attemptNo: 2,
    status: 'Failed',
    workerName: 'StockSyncWorker',
    workerInstanceId: 'StockSyncWorker@host-1:4242',
    errorCode: 'channel_rejected',
    errorMessage: 'The channel rejected the stock update.',
  }),
});

/** The detail of {@link failedSync}: two attempts and a message whose `lastError` must never reach the screen. */
export function failedSyncDetail(overrides: Partial<ProcessingTaskDetail> = {}): ProcessingTaskDetail {
  return task({
    taskId: SYNC_TASK_ID,
    taskType: 'StockSync',
    status: 'Failed',
    orderId: null,
    channelId: POS_ID,
    productId: PRODUCT_ID,
    productSku: 'TEA-01',
    retryCount: 1,
    correlationId: 'corr-sync-7',
    messageId: 'message-7',
    createdAt: new Date(2026, 9, 2, 9, 30).toISOString(),
    updatedAt: new Date(2026, 9, 2, 9, 30, 4).toISOString(),
    attempts: [
      attempt({
        attemptNo: 1,
        status: 'Failed',
        workerName: 'StockSyncWorker',
        workerInstanceId: 'StockSyncWorker@host-1:4242',
        errorCode: 'channel_unavailable',
        errorMessage: 'The channel did not answer.',
      }),
      attempt({
        attemptNo: 2,
        status: 'Failed',
        workerName: 'StockSyncWorker',
        workerInstanceId: 'StockSyncWorker@host-2:777',
        errorCode: 'channel_rejected',
        errorMessage: 'The channel rejected the stock update.',
      }),
    ],
    message: {
      outboxEventId: 'message-7',
      eventType: 'stock.sync.requested',
      status: 'Published',
      createdAt: new Date(2026, 9, 2, 9, 30).toISOString(),
      publishedAt: new Date(2026, 9, 2, 9, 30, 1).toISOString(),
      publishAttempts: 3,
      lastError: 'INTERNAL broker text that is not sanitised',
    },
    ...overrides,
  });
}

/** `GET /api/processing/tasks`; records the query string of every request. `answer` can replace the response. */
export function serveTaskList(initial: ProcessingTaskSummary[], totalCount = initial.length) {
  const state: { items: ProcessingTaskSummary[]; totalCount: number; answer?: () => Response } = {
    items: initial,
    totalCount,
  };
  const requests: Record<string, string>[] = [];
  server.use(
    http.get(api('/processing/tasks'), ({ request }) => {
      const url = new URL(request.url);
      requests.push(Object.fromEntries(url.searchParams));
      if (state.answer) {
        return state.answer();
      }

      const page: PagedResult<ProcessingTaskSummary> = {
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

/**
 * `GET /api/processing/tasks/{id}` and `POST /api/processing/tasks/{id}/retry`. `detail` is what the next read
 * returns (null = 404); `retryAnswer` decides the response of a retry (the default accepts it and makes the task
 * `Pending`, as the API does).
 */
export function serveTask(initial: ProcessingTaskDetail | null) {
  const state: { detail: ProcessingTaskDetail | null; retryAnswer?: () => Response } = { detail: initial };
  const reads: string[] = [];
  const retries: string[] = [];
  server.use(
    http.get(api('/processing/tasks/:id'), ({ params }) => {
      reads.push(String(params.id));
      return state.detail ? HttpResponse.json(state.detail) : problem(404, { errorCode: 'task_not_found' });
    }),
    http.post(api('/processing/tasks/:id/retry'), ({ params }) => {
      retries.push(String(params.id));
      if (state.retryAnswer) {
        return state.retryAnswer();
      }

      if (state.detail) {
        state.detail = { ...state.detail, status: 'Pending', messageId: 'message-8' };
      }

      return HttpResponse.json(
        { taskId: String(params.id), status: 'Pending', messageId: 'message-8' },
        { status: 202 },
      );
    }),
  );
  return { state, reads, retries };
}
