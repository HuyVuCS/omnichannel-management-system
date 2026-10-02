import { baseApi } from '@/shared/api/baseApi';
import { isApiError, isUnexpectedError, type PagedResult } from '@/shared/api/types';
import type { SortDirection } from '@/shared/hooks/useListQueryState';

/** Values of the API's processing enums (serialised as strings). */
export const TASK_TYPES = ['OrderProcessing', 'StockSync', 'Invoice', 'Email'] as const;
export type TaskType = (typeof TASK_TYPES)[number];

export const TASK_STATUSES = ['Pending', 'InProgress', 'RetryScheduled', 'Succeeded', 'Failed'] as const;
export type TaskStatus = (typeof TASK_STATUSES)[number];

export const ATTEMPT_STATUSES = ['Running', 'Succeeded', 'Failed'] as const;
export type AttemptStatus = (typeof ATTEMPT_STATUSES)[number];

export const MESSAGE_STATUSES = ['Pending', 'Published', 'Failed'] as const;
export type MessageStatus = (typeof MESSAGE_STATUSES)[number];

/** A task in one of these states is finished: nothing changes any more without a manual retry. */
export function isTerminalTaskStatus(status: TaskStatus): boolean {
  return status === 'Succeeded' || status === 'Failed';
}

/** One execution of a task by a worker instance. */
export interface ProcessingAttempt {
  attemptNo: number;
  status: AttemptStatus;
  workerName: string;
  workerInstanceId: string;
  errorCode: string | null;
  /** Sanitised by the API: safe to show. */
  errorMessage: string | null;
  startedAt: string;
  finishedAt: string | null;
}

/** Publish state of the task's current message (its outbox row). */
export interface ProcessingMessage {
  outboxEventId: string;
  eventType: string;
  /** `Pending` = not handed to RabbitMQ yet, `Published` = handed over, `Failed` = the publisher gave up. */
  status: MessageStatus;
  createdAt: string;
  publishedAt: string | null;
  publishAttempts: number;
  /** Not sanitised for the UI: never shown. */
  lastError: string | null;
}

/** What the list and the detail of a task share. */
interface ProcessingTaskBase {
  taskId: string;
  taskType: TaskType;
  status: TaskStatus;
  /** Set once the Order Worker has created the order; null for tasks that belong to no order (stock sync). */
  orderId: string | null;
  /** The channel of a stock-sync task; null for other task types. */
  channelId: string | null;
  /** The product of a stock-sync task; null for other task types. */
  productId: string | null;
  /** The SKU of that product; null when the task has no product. */
  productSku: string | null;
  retryCount: number;
  attemptBudgetStart: number;
  correlationId: string;
  messageId: string | null;
  createdAt: string;
  updatedAt: string | null;
}

/** A row of `GET /api/processing/tasks`. */
export interface ProcessingTaskSummary extends ProcessingTaskBase {
  attemptCount: number;
  /** The attempt with the highest number; null when none was started yet. */
  lastAttempt: ProcessingAttempt | null;
}

/** `GET /api/processing/tasks/{id}`: the task, all its attempts (ordered by number) and its message. */
export interface ProcessingTaskDetail extends ProcessingTaskBase {
  attempts: ProcessingAttempt[];
  message: ProcessingMessage | null;
}

/** Sort fields of `GET /api/processing/tasks`. Without a sort the API answers newest first (`createdAt`, descending). */
export const PROCESSING_SORT_FIELDS = ['createdAt', 'updatedAt'] as const;
export type ProcessingSortField = (typeof PROCESSING_SORT_FIELDS)[number];

/**
 * `from`/`to` are ISO 8601 instants (inclusive, on the creation time); `errorCode` matches a task one of whose attempts
 * failed with that code.
 */
export interface ProcessingTaskListArgs {
  page: number;
  pageSize: number;
  sortBy?: ProcessingSortField;
  sortDir?: SortDirection;
  taskType?: TaskType;
  status?: TaskStatus;
  channelId?: string;
  correlationId?: string;
  errorCode?: string;
  from?: string;
  to?: string;
}

/** 202 of a manual retry: the task is `Pending` again and waits for its new message. */
export interface RetryProcessingTaskResponse {
  taskId: string;
  status: TaskStatus;
  messageId: string;
}

/** Every task list; a single task is tagged with its id. Realtime hints invalidate both (`features/realtime`). */
export const PROCESSING_LIST_TAG = { type: 'ProcessingTask', id: 'LIST' } as const;

function compact(params: Record<string, string | number | undefined>) {
  return Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined && value !== ''));
}

export const processingApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    listProcessingTasks: build.query<PagedResult<ProcessingTaskSummary>, ProcessingTaskListArgs>({
      query: (args) => ({ url: 'processing/tasks', params: compact({ ...args }) }),
      providesTags: [PROCESSING_LIST_TAG],
    }),
    getProcessingTask: build.query<ProcessingTaskDetail, string>({
      query: (taskId) => `processing/tasks/${taskId}`,
      providesTags: (_result, _error, taskId) => [{ type: 'ProcessingTask', id: taskId }],
    }),
    /**
     * Manual retry of a `Failed` task (Manager/Admin). Whatever the API answers about the task - accepted, "not failed
     * any more", "does not exist" - what is on screen about it is stale, so the task, the task lists, its order (the
     * order detail lists its tasks) and the summary are fetched again. Only a network error or a 5xx changes nothing.
     */
    retryProcessingTask: build.mutation<RetryProcessingTaskResponse, { taskId: string; orderId?: string | null }>({
      query: ({ taskId }) => ({ url: `processing/tasks/${taskId}/retry`, method: 'POST' }),
      invalidatesTags: (_result, error, { taskId, orderId }) =>
        isApiError(error) && isUnexpectedError(error)
          ? []
          : [
              { type: 'ProcessingTask', id: taskId },
              PROCESSING_LIST_TAG,
              'DashboardSummary',
              ...(orderId ? [{ type: 'Order' as const, id: orderId }] : []),
            ],
    }),
  }),
});

export const { useListProcessingTasksQuery, useGetProcessingTaskQuery, useRetryProcessingTaskMutation } = processingApi;
