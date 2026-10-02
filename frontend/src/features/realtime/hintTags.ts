import type { HintBatch, HintEvent } from './hub';

/** The cache tags realtime hints can make stale. */
export const REALTIME_TAG_TYPES = [
  'Order',
  'Inventory',
  'InventoryMovement',
  'Product',
  'ProcessingTask',
  'DashboardSummary',
] as const;
export type RealtimeTagType = (typeof REALTIME_TAG_TYPES)[number];

/** A bare type = every cache entry of that type; with an id = the entries that provide exactly this tag. */
export type RealtimeTag = RealtimeTagType | { type: RealtimeTagType; id: string };

/** The id every list of a type provides (`ordersApi`, `productsApi`, `processingApi`). */
const LIST = 'LIST';

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
        'DashboardSummary',
      ];

    case 'processing.task.changed':
      if (event.all || typeof event.taskId !== 'string') {
        // Which orders the tasks belong to is not known either.
        return ['ProcessingTask', 'Order', 'DashboardSummary'];
      }

      return [
        { type: 'ProcessingTask', id: event.taskId },
        { type: 'ProcessingTask', id: LIST },
        // The order detail shows the tasks of its order.
        ...(typeof event.orderId === 'string' ? [{ type: 'Order' as const, id: event.orderId }] : []),
        'DashboardSummary',
      ];

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

/** How long tags are collected before they are invalidated together. */
export const FLUSH_WINDOW_MS = 1000;

/**
 * Collects tags and hands them to `flush` at most once per window: the first tag opens the window, everything that
 * arrives until it closes goes out as one union (each tag once; a bare type replaces its tags with ids). A burst of
 * hub messages - a stock import fans out into many stock-sync transitions - becomes one round of refetches per window
 * instead of one per message.
 */
export function createTagCoalescer(flush: (tags: RealtimeTag[]) => void, windowMs: number = FLUSH_WINDOW_MS) {
  const pending = new Map<string, RealtimeTag>();
  let timer: number | undefined;

  const flushNow = () => {
    timer = undefined;
    const tags = [...pending.values()].filter((tag) => typeof tag === 'string' || !pending.has(tag.type));
    pending.clear();
    flush(tags);
  };

  return {
    add(tags: readonly RealtimeTag[]) {
      for (const tag of tags) {
        pending.set(keyOf(tag), tag);
      }

      if (pending.size > 0 && timer === undefined) {
        timer = window.setTimeout(flushNow, windowMs);
      }
    },
    /** Drops what was collected (the connection is given up). */
    cancel() {
      window.clearTimeout(timer);
      timer = undefined;
      pending.clear();
    },
  };
}
