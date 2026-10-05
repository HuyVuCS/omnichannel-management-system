import type { HintBatch, HintEvent } from './hub';

/** The cache tags realtime hints can make stale. */
export const REALTIME_TAG_TYPES = [
  'Order',
  'Inventory',
  'InventoryMovement',
  'Product',
  'ProcessingTask',
  'ReportExport',
  'Alert',
  'DashboardSummary',
] as const;
export type RealtimeTagType = (typeof REALTIME_TAG_TYPES)[number];

/** A bare type = every cache entry of that type; with an id = the entries that provide exactly this tag. */
export type RealtimeTag = RealtimeTagType | { type: RealtimeTagType; id: string };

/** The id every list of a type provides (`ordersApi`, `productsApi`, `processingApi`, `alertsApi`, `reportsApi`). */
const LIST = 'LIST';

/** The task type of an export. The id of such a task is the id of its export (`reportsApi`). */
const REPORT_EXPORT_TASK = 'ReportExport';

/**
 * Everything realtime keeps fresh. Invalidated after every (re)connect and on `resync: true`: hints sent while there
 * was no connection, or left out of a burst, are lost. Never the whole cache: users, customers and channels do not
 * change in the background.
 */
export const RESYNC_TAGS: readonly RealtimeTag[] = REALTIME_TAG_TYPES;

/**
 * The cache tags one hint makes stale. Hints carry ids only; RTK Query refetches what is on screen through the API,
 * with the user's own authorization. `all: true` = the same tags without ids; an unknown event type = nothing.
 */
export function tagsForEvent(event: HintEvent): RealtimeTag[] {
  switch (event.type) {
    case 'order.created':
      return [event.all ? 'Order' : { type: 'Order', id: LIST }, 'DashboardSummary'];

    case 'inventory.changed':
      return [
        'Inventory',
        'InventoryMovement',
        event.all ? 'Product' : { type: 'Product', id: LIST },
        // A low-stock alert shows the product's stock as it is now, which changes without the alert changing.
        event.all ? 'Alert' : { type: 'Alert', id: LIST },
        'DashboardSummary',
      ];

    case 'processing.task.changed':
      if (event.all || typeof event.taskId !== 'string') {
        // Which orders the tasks belong to is not known either - nor whether exports are among them.
        return ['ProcessingTask', 'Order', 'ReportExport', 'DashboardSummary'];
      }

      return [
        { type: 'ProcessingTask', id: event.taskId },
        { type: 'ProcessingTask', id: LIST },
        // The order detail shows the tasks of its order.
        ...(typeof event.orderId === 'string' ? [{ type: 'Order' as const, id: event.orderId }] : []),
        // An export shows the status of its task; tasks of other types leave the exports alone.
        ...(event.taskType === REPORT_EXPORT_TASK
          ? [
              { type: 'ReportExport' as const, id: event.taskId },
              { type: 'ReportExport' as const, id: LIST },
            ]
          : []),
        'DashboardSummary',
      ];

    case 'alert.changed':
      if (event.all || typeof event.alertId !== 'string') {
        return ['Alert', 'DashboardSummary'];
      }

      // The summary counts the unresolved alerts.
      return [{ type: 'Alert', id: event.alertId }, { type: 'Alert', id: LIST }, 'DashboardSummary'];

    // Sent with an `alert.changed` for the same alert in the same batch; mapped anyway, so the client stays correct
    // if that ever changes. It only refreshes what is on screen: no toast, sound or badge.
    case 'low_stock.created':
      return [event.all ? 'Alert' : { type: 'Alert', id: LIST }, 'DashboardSummary'];

    default:
      return [];
  }
}

/** The tags of one `hints` message. Anything that is not a hint batch (a newer server, a bug) yields nothing. */
export function tagsForBatch(batch: HintBatch | null | undefined): RealtimeTag[] {
  if (typeof batch !== 'object' || batch === null) {
    return [];
  }

  const events = Array.isArray(batch.events) ? batch.events : [];
  return [
    ...(batch.resync === true ? RESYNC_TAGS : []),
    ...events.flatMap((event) => (typeof event === 'object' && event !== null ? tagsForEvent(event) : [])),
  ];
}

const keyOf = (tag: RealtimeTag) => (typeof tag === 'string' ? tag : `${tag.type}\n${tag.id}`);

/** The shortest time between two invalidations: tags that arrive sooner after a flush wait for the end of this window. */
export const FLUSH_WINDOW_MS = 1000;

/**
 * Hands tags to `flush` at most once per window (a throttle): when no flush happened within the last window, tags go
 * out at once; a flush opens a window, and everything that arrives until it closes goes out together when it does, as
 * one union (each tag once; a bare type replaces its tags with ids) - which opens the next window. A burst of hub
 * messages - a stock import fans out into many stock-sync transitions - becomes one round of refetches per window
 * instead of one per message, and a single change while nothing else happens is not held back.
 */
export function createTagCoalescer(flush: (tags: RealtimeTag[]) => void, windowMs: number = FLUSH_WINDOW_MS) {
  const pending = new Map<string, RealtimeTag>();
  /** Runs while the window of the last flush is open. */
  let timer: number | undefined;

  const flushNow = () => {
    const tags = [...pending.values()].filter((tag) => typeof tag === 'string' || !pending.has(tag.type));
    pending.clear();
    timer = window.setTimeout(windowEnded, windowMs);
    flush(tags);
  };

  const windowEnded = () => {
    timer = undefined;
    if (pending.size > 0) {
      flushNow();
    }
  };

  return {
    add(tags: readonly RealtimeTag[]) {
      for (const tag of tags) {
        pending.set(keyOf(tag), tag);
      }

      if (pending.size > 0 && timer === undefined) {
        flushNow();
      }
    },
    /** Drops what is pending (the connection is given up). An open window ends with nothing to flush. */
    cancel() {
      pending.clear();
    },
  };
}
