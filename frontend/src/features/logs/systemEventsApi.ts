import { baseApi } from '@/shared/api/baseApi';
import type { PagedResult } from '@/shared/api/types';
import type { SortDirection } from '@/shared/hooks/useListQueryState';

/** Values of the API's `SystemEventLevel` (serialised as strings), least severe first. */
export const EVENT_LEVELS = ['Information', 'Warning', 'Error', 'Critical'] as const;
export type EventLevel = (typeof EVENT_LEVELS)[number];

/**
 * The event types this client has a label and a summary for - the ones the API's allowlist (`SystemEventDetails`)
 * knows today. It is a list of texts, not a rule: a type that is missing here is still listed, under its raw name and
 * without a summary.
 */
export const EVENT_TYPES = [
  'inventory.imported',
  'order.insufficient_stock',
  'order.already_exists',
  'order.message_rejected',
  'stock_sync.message_rejected',
  'processing.failed',
  'processing.retry_requested',
  'outbox.publish_failed',
  'alert.acknowledged',
  'report.export_requested',
  'report_export.message_rejected',
  'user.created',
  'user.role_changed',
  'user.locked',
  'user.unlocked',
  'user.password_reset',
] as const;
export type KnownEventType = (typeof EVENT_TYPES)[number];

/** True for a type of {@link EVENT_TYPES}. */
export function isKnownEventType(eventType: string): eventType is KnownEventType {
  return (EVENT_TYPES as readonly string[]).includes(eventType);
}

/**
 * User management events. The API returns them to an Admin only (for a Manager they do not exist, whatever is asked
 * for); here the prefix only decides which options the event-type filter offers.
 */
export const USER_EVENT_PREFIX = 'user.';

/**
 * An event as `GET /api/system-events` returns it (`SystemEventResponse`). `details` holds the payload fields the API
 * allows for the event type - never the stored payload - and is null for a type the API has no entry for.
 */
export interface SystemEvent {
  logId: number;
  level: EventLevel;
  eventType: string;
  createdAt: string;
  correlationId: string | null;
  taskId: string | null;
  orderId: string | null;
  workerName: string | null;
  details: Record<string, unknown> | null;
  /** Who did it; null when the event names nobody or the actor is no user. */
  actorEmail: string | null;
  /** Whom it was done to (user management events). */
  targetEmail: string | null;
}

/** The only sort field of `GET /api/system-events`; newest first unless `asc`. */
export const SYSTEM_EVENT_SORT_FIELDS = ['createdAt'] as const;
export type SystemEventSortField = (typeof SYSTEM_EVENT_SORT_FIELDS)[number];

/**
 * `SystemEventListQuery`. `level` and `eventType` are repeated parameters (an event type is matched exactly);
 * `from`/`to` are ISO 8601 instants on the creation time (inclusive; at most 92 days when both are given - the
 * server's rule).
 */
export interface SystemEventListArgs {
  page: number;
  pageSize: number;
  sortBy?: SystemEventSortField;
  sortDir?: SortDirection;
  level?: readonly EventLevel[];
  eventType?: readonly string[];
  correlationId?: string;
  orderId?: string;
  taskId?: string;
  from?: string;
  to?: string;
}

/** Every event list. There is no realtime hint for events: a list is fetched again when it is opened and on "Refresh". */
export const SYSTEM_EVENT_LIST_TAG = { type: 'SystemEvent', id: 'LIST' } as const;

function listQuery({ level = [], eventType = [], ...single }: SystemEventListArgs): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(single)) {
    if (value !== undefined && value !== '') {
      params.set(key, String(value));
    }
  }

  // One parameter per value (`level=Error&level=Warning`), as the API binds lists.
  for (const value of level) {
    params.append('level', value);
  }

  for (const value of eventType) {
    params.append('eventType', value);
  }

  return params.toString();
}

export const systemEventsApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    listSystemEvents: build.query<PagedResult<SystemEvent>, SystemEventListArgs>({
      query: (args) => `system-events?${listQuery(args)}`,
      providesTags: [SYSTEM_EVENT_LIST_TAG],
    }),
  }),
});

export const { useListSystemEventsQuery } = systemEventsApi;
