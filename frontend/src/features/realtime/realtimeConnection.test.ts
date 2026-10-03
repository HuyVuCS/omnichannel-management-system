import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createAppStore } from '@/app/store';
import { selectSession, sessionStarted } from '@/features/auth/authSlice';
import { saveSession } from '@/features/auth/session';
import { baseApi } from '@/shared/api/baseApi';
import { fakeHub } from '@/test/hub';
import { managerUser, sessionOf, staffUser } from '@/test/server';
import { FLUSH_WINDOW_MS, RESYNC_TAGS } from './hintTags';
import { RECONNECT_DELAYS_MS, startRealtime } from './realtimeConnection';
import { selectConnectionState } from './realtimeSlice';

const TASK = '0199a1b2-0000-7000-8000-0000000000aa';

/**
 * The connection manager without React: a store with a session and a fake hub. Fake timers (`setTimeout` only) because
 * the reconnect delays (up to 30 s) and the one-second flush window are jumped over and asserted to the millisecond.
 * The flush is a throttle: at once when nothing was flushed in the last second, otherwise at the end of that second.
 */
describe('realtime connection', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function start() {
    saveSession(sessionOf(staffUser));
    const store = createAppStore();
    const hub = fakeHub();
    const invalidate = vi.spyOn(baseApi.util, 'invalidateTags');
    const stop = startRealtime(store, hub.create);
    return { store, hub, invalidate, stop, state: () => selectConnectionState(store.getState()) };
  }

  /** Lets `ms` pass and everything that follows from it settle. */
  const wait = (ms: number) => vi.advanceTimersByTimeAsync(ms);

  it('connects with the current token, is live once the hub accepted, and invalidates the realtime tags once', async () => {
    const { hub, invalidate, state, stop } = start();
    await wait(0);

    expect(hub.connections).toHaveLength(1);
    expect(hub.latest().token).toBe(sessionOf(staffUser).accessToken);
    expect(state()).toBe('connecting');

    await hub.latest().accept();
    expect(state()).toBe('connected');

    // Through the same throttle as hints: at once (nothing was flushed before), and exactly once.
    expect(invalidate).toHaveBeenCalledTimes(1);
    expect(invalidate).toHaveBeenCalledWith(RESYNC_TAGS);
    await wait(2 * FLUSH_WINDOW_MS);
    expect(invalidate).toHaveBeenCalledTimes(1);

    await stop();
    expect(hub.latest().stopped).toBe(true);
    expect(state()).toBe('disconnected');
  });

  it('invalidates the hint of an idle second at once and the hints of a burst once per second', async () => {
    const { hub, invalidate } = start();
    await wait(0);
    await hub.latest().accept();
    // The second of the connect resync is over: idle.
    await wait(FLUSH_WINDOW_MS);
    invalidate.mockClear();

    // Idle: the first message is not held back.
    await hub.latest().send([{ type: 'order.created', orderId: 'o1' }]);
    expect(invalidate).toHaveBeenCalledTimes(1);
    expect(invalidate).toHaveBeenLastCalledWith([{ type: 'Order', id: 'LIST' }, 'DashboardSummary']);

    // What follows within that second waits for its end and goes out as one union.
    await wait(300);
    await hub.latest().send([{ type: 'processing.task.changed', taskId: TASK, taskType: 'StockSync' }]);
    await wait(300);
    await hub.latest().send([{ type: 'unknown.event' }, { type: 'inventory.changed', productIds: ['p1'] }]);
    await wait(FLUSH_WINDOW_MS - 600 - 1);
    expect(invalidate).toHaveBeenCalledTimes(1);

    await wait(1);
    expect(invalidate).toHaveBeenCalledTimes(2);
    expect(invalidate).toHaveBeenLastCalledWith([
      { type: 'ProcessingTask', id: TASK },
      { type: 'ProcessingTask', id: 'LIST' },
      'DashboardSummary',
      'Inventory',
      'InventoryMovement',
      { type: 'Product', id: 'LIST' },
      { type: 'Alert', id: 'LIST' },
    ]);

    // resync: every realtime tag, never the whole cache - and through the same throttle (the second of the flush
    // above is still running).
    const reset = vi.spyOn(baseApi.util, 'resetApiState');
    await hub.latest().send({ events: [], resync: true });
    expect(invalidate).toHaveBeenCalledTimes(2);
    await wait(FLUSH_WINDOW_MS);
    expect(invalidate).toHaveBeenCalledTimes(3);
    expect(invalidate).toHaveBeenLastCalledWith(RESYNC_TAGS);
    expect(reset).not.toHaveBeenCalled();
  });

  it('reconnects after 0, 2, 5, 10, 30 seconds, then every 30 seconds, and resyncs when it is live again', async () => {
    const { hub, invalidate, state } = start();
    await wait(0);
    await hub.latest().accept();
    await wait(FLUSH_WINDOW_MS);
    invalidate.mockClear();

    await hub.latest().drop();
    expect(state()).toBe('reconnecting');

    // Every attempt fails; the next one starts exactly when its delay is over.
    let attempts = 1;
    for (const delay of [...RECONNECT_DELAYS_MS, 30_000, 30_000]) {
      if (delay > 0) {
        await wait(delay - 1);
        expect(hub.connections).toHaveLength(attempts);
      }

      await wait(delay === 0 ? 0 : 1);
      attempts += 1;
      expect(hub.connections).toHaveLength(attempts);
      // The failed connection was closed before the next one was created: never two at once.
      expect(hub.usage.maxOpen).toBe(1);
      // The quick retries are "reconnecting"; from the first 30-second wait on the user is told "offline".
      expect(state()).toBe(attempts - 1 >= RECONNECT_DELAYS_MS.length ? 'disconnected' : 'reconnecting');
      await hub.latest().fail();
    }

    expect(state()).toBe('disconnected');
    expect(invalidate).not.toHaveBeenCalled();

    // The server is back: live, and what was missed is fetched again - at once (nothing was flushed for a long time)
    // and once.
    await wait(30_000);
    await hub.latest().accept();
    expect(state()).toBe('connected');
    expect(invalidate).toHaveBeenCalledTimes(1);
    expect(invalidate).toHaveBeenCalledWith(RESYNC_TAGS);
    await wait(FLUSH_WINDOW_MS);
    expect(invalidate).toHaveBeenCalledTimes(1);

    // A later loss starts with the quick retries again.
    const before = hub.connections.length;
    await hub.latest().drop();
    await wait(0);
    expect(hub.connections).toHaveLength(before + 1);
    expect(state()).toBe('reconnecting');
  });

  it('retries a failed first start with the same delays', async () => {
    const { hub, state } = start();
    await wait(0);
    await hub.latest().fail();
    expect(state()).toBe('connecting');

    await wait(0);
    expect(hub.connections).toHaveLength(2);
    await hub.latest().fail();

    await wait(1999);
    expect(hub.connections).toHaveLength(2);
    await wait(1);
    expect(hub.connections).toHaveLength(3);
    expect(state()).toBe('connecting');

    await hub.latest().accept();
    expect(state()).toBe('connected');
  });

  it('ends the session when the hub refuses the token that is still the current one', async () => {
    const { store, hub, state } = start();
    await wait(0);

    await hub.latest().refuse();

    expect(selectSession(store.getState())).toBeNull();
    expect(state()).toBe('disconnected');
    // No retry on its own: the session is over (the manager component stops this connection).
    await wait(60_000);
    expect(hub.connections).toHaveLength(1);
  });

  it('keeps the session when the refused token is not the current one any more', async () => {
    const { store, hub } = start();
    await wait(0);
    const newer = sessionOf(managerUser);
    store.dispatch(sessionStarted(newer));

    await hub.latest().refuse();

    expect(selectSession(store.getState())).toEqual(newer);
  });

  it('does nothing any more after stop: no retry, no invalidation, a running start is given up', async () => {
    const { hub, invalidate, stop } = start();
    await wait(0);
    await hub.latest().accept();
    // The connect resync went out at once; a hint within its second is pending when the connection is stopped.
    invalidate.mockClear();
    await hub.latest().send([{ type: 'order.created', orderId: 'o1' }]);

    await stop();
    await hub.latest().drop();
    await wait(120_000);

    expect(hub.connections).toHaveLength(1);
    expect(invalidate).not.toHaveBeenCalled();
  });

  it('starts only after the previous connection has stopped', async () => {
    saveSession(sessionOf(staffUser));
    const store = createAppStore();
    const hub = fakeHub();
    let previousStopped = () => {};
    const previous = new Promise<void>((resolve) => (previousStopped = resolve));

    startRealtime(store, hub.create, previous);
    await wait(1000);
    expect(hub.connections).toHaveLength(0);

    previousStopped();
    await wait(0);
    expect(hub.connections).toHaveLength(1);
  });
});
