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

describe('hints → cache tags', () => {
  it('order.created makes the order lists and the summary stale', () => {
    expect(tagsForEvent({ type: 'order.created', orderId: ORDER, channelId: 'c', status: 'Reserved' })).toEqual([
      { type: 'Order', id: 'LIST' },
      'DashboardSummary',
    ]);
  });

  it('inventory.changed makes stock, movements, the product lists and the summary stale', () => {
    expect(tagsForEvent({ type: 'inventory.changed', productIds: ['p1', 'p2'] })).toEqual([
      'Inventory',
      'InventoryMovement',
      { type: 'Product', id: 'LIST' },
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
      'DashboardSummary',
    ]);
    expect(tagsForEvent({ type: 'processing.task.changed', all: true })).toEqual([
      'ProcessingTask',
      'Order',
      'DashboardSummary',
    ]);
    // A task event that names no task cannot be narrowed down either.
    expect(tagsForEvent({ type: 'processing.task.changed' })).toEqual(['ProcessingTask', 'Order', 'DashboardSummary']);
  });

  it('ignores event types it does not know', () => {
    expect(tagsForEvent({ type: 'low_stock.created', productId: 'p1' })).toEqual([]);
    expect(tagsForBatch({ events: [{ type: 'something.new', all: true }], resync: false })).toEqual([]);
  });

  it('resync means every realtime tag, and only those', () => {
    expect(RESYNC_TAGS).toEqual([
      'Order',
      'Inventory',
      'InventoryMovement',
      'Product',
      'ProcessingTask',
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
describe('coalescing', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('flushes several messages of one window as one union, and the next ones after the next window', () => {
    const flushed: RealtimeTag[][] = [];
    const coalescer = createTagCoalescer((tags) => flushed.push(tags));

    coalescer.add(tagsForEvent({ type: 'order.created', orderId: ORDER }));
    vi.advanceTimersByTime(400);
    coalescer.add(tagsForEvent({ type: 'processing.task.changed', taskId: TASK, orderId: ORDER }));
    vi.advanceTimersByTime(400);
    coalescer.add(tagsForEvent({ type: 'order.created', orderId: 'another-order' }));
    vi.advanceTimersByTime(FLUSH_WINDOW_MS - 800 - 1);
    expect(flushed).toEqual([]);

    vi.advanceTimersByTime(1);
    expect(flushed).toEqual([
      [
        { type: 'Order', id: 'LIST' },
        'DashboardSummary',
        { type: 'ProcessingTask', id: TASK },
        { type: 'ProcessingTask', id: 'LIST' },
        { type: 'Order', id: ORDER },
      ],
    ]);

    // Nothing collected: nothing flushed, however long it takes.
    vi.advanceTimersByTime(10 * FLUSH_WINDOW_MS);
    expect(flushed).toHaveLength(1);

    coalescer.add(tagsForEvent({ type: 'inventory.changed', productIds: ['p1'] }));
    vi.advanceTimersByTime(FLUSH_WINDOW_MS - 1);
    expect(flushed).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(flushed[1]).toEqual(['Inventory', 'InventoryMovement', { type: 'Product', id: 'LIST' }, 'DashboardSummary']);
  });

  it('replaces the tags with ids of a type by the bare type (resync within the window)', () => {
    const flushed: RealtimeTag[][] = [];
    const coalescer = createTagCoalescer((tags) => flushed.push(tags));

    coalescer.add(tagsForEvent({ type: 'processing.task.changed', taskId: TASK, orderId: ORDER }));
    coalescer.add(tagsForBatch({ events: [], resync: true }));
    vi.advanceTimersByTime(FLUSH_WINDOW_MS);

    expect(flushed).toHaveLength(1);
    expect([...(flushed[0] ?? [])].sort()).toEqual([...RESYNC_TAGS].sort());
  });

  it('flushes nothing after cancel', () => {
    const flush = vi.fn();
    const coalescer = createTagCoalescer(flush);

    coalescer.add(['Order']);
    coalescer.cancel();
    vi.advanceTimersByTime(2 * FLUSH_WINDOW_MS);

    expect(flush).not.toHaveBeenCalled();
  });
});
