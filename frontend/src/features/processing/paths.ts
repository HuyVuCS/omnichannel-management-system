export const PROCESSING_PATH = '/processing';

/** Route pattern of the task detail page. */
export const PROCESSING_TASK_ROUTE = `${PROCESSING_PATH}/tasks/:taskId`;

/**
 * The API lets Manager and Admin retry a failed task; everyone signed in may read tasks. Kept next to the paths (not in
 * processingApi), like the roles of the stock import.
 */
export const RETRY_ROLES = ['Admin', 'Manager'] as const;

/** The detail page of one task. */
export function processingTaskPath(taskId: string): string {
  return `${PROCESSING_PATH}/tasks/${encodeURIComponent(taskId)}`;
}

/** Filters (and sort) of the task list as the address keeps them. */
export interface ProcessingListFilter {
  taskType?: string;
  status?: string;
  channelId?: string;
  productId?: string;
  correlationId?: string;
  errorCode?: string;
  from?: string;
  to?: string;
  sortBy?: string;
  sortDir?: string;
}

/** The task list with filters, e.g. from a number of the dashboard. */
export function processingListPath(filter: ProcessingListFilter = {}): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(filter)) {
    if (value) {
      params.set(key, value);
    }
  }

  const query = params.toString();
  return query ? `${PROCESSING_PATH}?${query}` : PROCESSING_PATH;
}

/**
 * Router state of a link from the task list to a detail page: the list's address (path + query), so "Back" returns to
 * the same page, sort and filters.
 */
export interface TaskDetailLocationState {
  from: string;
}

/** The list address a detail page was opened from, when there is one and it really is an address of the task list. */
export function processingListAddress(state: unknown): string | undefined {
  const from = (state as Partial<TaskDetailLocationState> | null)?.from;
  return typeof from === 'string' && (from === PROCESSING_PATH || from.startsWith(`${PROCESSING_PATH}?`))
    ? from
    : undefined;
}
