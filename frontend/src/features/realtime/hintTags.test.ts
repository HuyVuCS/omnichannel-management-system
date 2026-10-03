import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  createTagCoalescer,
  FLUSH_WINDOW_MS,
  RESYNC_TAGS,
  tagsForBatch,
  tagsForEvent,
  type RealtimeTag,
} from './hintTags';

const TASK = '0199a1b2-0000-7000-8000-0000000000aa';
const ORDER = '0199a1b2-0000-7000-8000-000000000001';
const ALERT = '0199a1b2-0000-7000-8000-0000000000e1';

describe('hints → cache tags', () => {
  it('order.created makes the order lists and the summary stale', () => {
    expect(tagsForEvent({ type: 'order.created', orderId: ORDER, channelId: 'c', status: 'Reserved' })).toEqual([
      { type: 'Order', id: 'LIST' },
      'DashboardSummary',
    ]);
  });

  it('inventory.changed makes stock, movements, the product lists, the alert lists and the summary stale', () => {
    expect(tagsForEvent({ type: 'inventory.changed', productIds: ['p1', 'p2'] })).toEqual([
      'Inventory',
      'InventoryMovement',
      { type: 'Product', id: 'LIST' },
      // A low-stock alert shows the stock of now, which changes without the alert changing.
      { type: 'Alert', id: 'LIST' },
      'DashboardSummary',
    ]);
  });

  it('alert.changed makes that alert, the alert lists and the summary stale', () => {
    expect(
      tagsForEvent({ type: 'alert.changed', alertId: ALERT, alertType: 'LowStock', status: 'Open', productId: 'p1' }),
    ).toEqual([{ type: 'Alert', id: ALERT }, { type: 'Alert', id: 'LIST' }, 'DashboardSummary']);
    // An alert event that names no alert cannot be narrowed down.
    expect(tagsForEvent({ type: 'alert.changed' })).toEqual(['Alert', 'DashboardSummary']);
  });

  it('low_stock.created makes the alert lists and the summary stale, also without the alert.changed it comes with', () => {
    expect(tagsForEvent({ type: 'low_stock.created', alertId: ALERT, productId: 'p1' })).toEqual([
      { type: 'Alert', id: 'LIST' },
      'DashboardSummary',
    ]);
    // The batch the server sends for a new low-stock alert: both events, each tag once after coalescing.
    expect(
      tagsForBatch({
        events: [
          { type: 'alert.changed', alertId: ALERT, alertType: 'LowStock', status: 'Open', productId: 'p1' },
          { type: 'low_stock.created', alertId: ALERT, productId: 'p1' },
        ],
        resync: false,
      }),
    ).toEqual([
      { type: 'Alert', id: ALERT },
      { type: 'Alert', id: 'LIST' },
      'DashboardSummary',
      { type: 'Alert', id: 'LIST' },
      'DashboardSummary',
    ]);
  });

  it('processing.task.changed makes that task, the task lists and the summary stale - and its order when it has one', () => {
    expect(
      tagsForEvent({ type: 'processing.task.changed', taskId: TASK, taskType: 'StockSync', status: 'Failed' }),
    ).toEqual([{ type: 'ProcessingTask', id: TASK }, { type: 'ProcessingTask', id: 'LIST' }, 'DashboardSummary']);
    expect(
      tagsForEvent({ type: 'processing.task.changed', taskId: TASK, orderId: ORDER, taskType: 'OrderProcessing' }),
    ).toEqual([
      { type: 'ProcessingTask', id: TASK },
      { type: 'ProcessingTask', id: 'LIST' },
      { type: 'Order', id: ORDER },
      'DashboardSummary',
    ]);
  });

  it('all: true means every entry of the types of that event', () => {
    expect(tagsForEvent({ type: 'order.created', all: true })).toEqual(['Order', 'DashboardSummary']);
    expect(tagsForEvent({ type: 'inventory.changed', all: true })).toEqual([
      'Inventory',
      'InventoryMovement',
      'Product',
      'Alert',
      'DashboardSummary',
    ]);
    expect(tagsForEvent({ type: 'alert.changed', all: true })).toEqual(['Alert', 'DashboardSummary']);
    expect(tagsForEvent({ type: 'low_stock.created', all: true })).toEqual(['Alert', 'DashboardSummary']);
    expect(tagsForEvent({ type: 'processing.task.changed', all: true })).toEqual([
      'ProcessingTask',
      'Order',
      'DashboardSummary',
    ]);
    // A task event that names no task cannot be narrowed down either.
    expect(tagsForEvent({ type: 'processing.task.changed' })).toEqual(['ProcessingTask', 'Order', 'DashboardSummary']);
  });

  it('ignores event types it does not know', () => {
    expect(tagsForEvent({ type: 'order.status.changed', orderId: ORDER })).toEqual([]);
    expect(tagsForBatch({ events: [{ type: 'something.new', all: true }], resync: false })).toEqual([]);
  });

  it('resync means every realtime tag, and only those', () => {
    expect(RESYNC_TAGS).toEqual([
      'Order',
      'Inventory',
      'InventoryMovement',
      'Product',
      'ProcessingTask',
      'Alert',
      'DashboardSummary',
    ]);
    expect(tagsForBatch({ events: [], resync: true })).toEqual(RESYNC_TAGS);
  });

  it('yields nothing for a message that is not a hint batch', () => {
    expect(tagsForBatch(null)).toEqual([]);
    expect(tagsForBatch(undefined)).toEqual([]);
    expect(tagsForBatch('hints' as never)).toEqual([]);
    expect(tagsForBatch({ resync: false } as never)).toEqual([]);
    expect(tagsForBatch({ events: [null, 'x', { type: 'order.created' }], resync: false } as never)).toEqual([
      { type: 'Order', id: 'LIST' },
      'DashboardSummary',
    ]);
  });
});

/** Fake timers: the one-second window is jumped over, and "nothing before the window ends" can be asserted exactly. */
describe('coalescing (a throttle: at most one flush per window)', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  const orderCreated = (orderId: string) => tagsForEvent({ type: 'order.created', orderId });
  const taskChanged = tagsForEvent({ type: 'processing.task.changed', taskId: TASK, orderId: ORDER });
  const stockChanged = tagsForEvent({ type: 'inventory.changed', productIds: ['p1'] });

  it('flushes at once when nothing was flushed within the last window', () => {
    const flushed: RealtimeTag[][] = [];
    const coalescer = createTagCoalescer((tags) => flushed.push(tags));

    // The very first tags: no window is open.
    coalescer.add(orderCreated(ORDER));
    expect(flushed).toEqual([[{ type: 'Order', id: 'LIST' }, 'DashboardSummary']]);

    // The window of that flush has ended with nothing collected: idle again, so the next tags go out at once.
    vi.advanceTimersByTime(FLUSH_WINDOW_MS);
    expect(flushed).toHaveLength(1);
    coalescer.add(stockChanged);
    expect(flushed).toHaveLength(2);
    expect(flushed[1]).toEqual([
      'Inventory',
      'InventoryMovement',
      { type: 'Product', id: 'LIST' },
      { type: 'Alert', id: 'LIST' },
      'DashboardSummary',
    ]);

    // Nothing collected: nothing flushed, however long it takes.
    vi.advanceTimersByTime(10 * FLUSH_WINDOW_MS);
    expect(flushed).toHaveLength(2);
  });

  it('flushes what arrives within the window of a flush once, at its end, as one union', () => {
    const flushed: RealtimeTag[][] = [];
    const coalescer = createTagCoalescer((tags) => flushed.push(tags));

    coalescer.add(stockChanged);
    expect(flushed).toHaveLength(1);

    vi.advanceTimersByTime(300);
    coalescer.add(orderCreated(ORDER));
    vi.advanceTimersByTime(300);
    coalescer.add(taskChanged);
    vi.advanceTimersByTime(300);
    coalescer.add(orderCreated('another-order'));
    vi.advanceTimersByTime(FLUSH_WINDOW_MS - 900 - 1);
    expect(flushed).toHaveLength(1);

    vi.advanceTimersByTime(1);
    expect(flushed).toHaveLength(2);
    // Each tag once, in the order of its first arrival.
    expect(flushed[1]).toEqual([
      { type: 'Order', id: 'LIST' },
      'DashboardSummary',
      { type: 'ProcessingTask', id: TASK },
      { type: 'ProcessingTask', id: 'LIST' },
      { type: 'Order', id: ORDER },
    ]);

    // That flush opened a window of its own: tags right after it wait again.
    coalescer.add(stockChanged);
    expect(flushed).toHaveLength(2);
    vi.advanceTimersByTime(FLUSH_WINDOW_MS - 1);
    expect(flushed).toHaveLength(2);
    vi.advanceTimersByTime(1);
    expect(flushed).toHaveLength(3);
  });

  it('turns a burst over several windows into one flush per window', () => {
    const flushedAt: number[] = [];
    let now = 0;
    const coalescer = createTagCoalescer(() => flushedAt.push(now));

    // A message every 100 ms for 3.5 seconds. (`now` is the time at the end of each step: that is when a window that
    // closes during the step ends.)
    while (now < 3500) {
      coalescer.add(taskChanged);
      now += 100;
      vi.advanceTimersByTime(100);
    }

    expect(flushedAt).toEqual([0, 1000, 2000, 3000]);

    // The messages after the last flush go out when its window ends, and then it is quiet.
    vi.advanceTimersByTime(10 * FLUSH_WINDOW_MS);
    expect(flushedAt).toHaveLength(5);
  });

  it('replaces the tags with ids of a type by the bare type (resync within the window)', () => {
    const flushed: RealtimeTag[][] = [];
    const coalescer = createTagCoalescer((tags) => flushed.push(tags));

    coalescer.add(stockChanged);
    coalescer.add(taskChanged);
    coalescer.add(tagsForBatch({ events: [], resync: true }));
    vi.advanceTimersByTime(FLUSH_WINDOW_MS);

    expect(flushed).toHaveLength(2);
    expect([...(flushed[1] ?? [])].sort()).toEqual([...RESYNC_TAGS].sort());
  });

  it('flushes a resync at once when idle, like every other tag', () => {
    const flushed: RealtimeTag[][] = [];
    const coalescer = createTagCoalescer((tags) => flushed.push(tags));

    // What a (re)connect adds.
    coalescer.add(RESYNC_TAGS);

    expect(flushed).toEqual([[...RESYNC_TAGS]]);
  });

  it('drops what is pending on cancel, and still flushes at most once per window afterwards', () => {
    const flushed: RealtimeTag[][] = [];
    const coalescer = createTagCoalescer((tags) => flushed.push(tags));

    coalescer.add(['Order']);
    coalescer.add(['Inventory']);
    coalescer.cancel();
    vi.advanceTimersByTime(FLUSH_WINDOW_MS - 1);
    expect(flushed).toEqual([['Order']]);

    // The window of the flush before the cancel is still open.
    coalescer.add(['Product']);
    expect(flushed).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(flushed).toEqual([['Order'], ['Product']]);
  });

  it('flushes nothing for an empty batch', () => {
    const flush = vi.fn();
    const coalescer = createTagCoalescer(flush);

    coalescer.add([]);
    vi.advanceTimersByTime(2 * FLUSH_WINDOW_MS);

    expect(flush).not.toHaveBeenCalled();
  });
});
