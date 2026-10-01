import { baseApi } from '@/shared/api/baseApi';

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

/** `GET /api/processing/tasks/{id}`: the task, all its attempts (ordered by number) and its message. */
export interface ProcessingTaskDetail {
  taskId: string;
  taskType: TaskType;
  status: TaskStatus;
  /** Set once the Order Worker has created the order. */
  orderId: string | null;
  retryCount: number;
  attemptBudgetStart: number;
  correlationId: string;
  messageId: string | null;
  createdAt: string;
  updatedAt: string | null;
  attempts: ProcessingAttempt[];
  message: ProcessingMessage | null;
}

export const processingApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    getProcessingTask: build.query<ProcessingTaskDetail, string>({
      query: (taskId) => `processing/tasks/${taskId}`,
      providesTags: (_result, _error, taskId) => [{ type: 'ProcessingTask', id: taskId }],
    }),
  }),
});

export const { useGetProcessingTaskQuery } = processingApi;
