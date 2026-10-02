import type { AppStore } from '@/app/store';
import { loggedOut, selectAccessToken } from '@/features/auth/authSlice';
import { baseApi } from '@/shared/api/baseApi';
import { createTagCoalescer, RESYNC_TAGS, tagsForBatch } from './hintTags';
import { HubUnauthorizedError, type HubConnection, type HubFactory } from './hub';
import { connectionChanged, type ConnectionState } from './realtimeSlice';

/**
 * Waiting time before each attempt after a lost connection or a failed start: at once, then 2, 5, 10, 30 seconds, then
 * every 30 seconds for as long as the session lasts. The connection never gives up on its own: the access token has
 * no refresh, so the session ends at its expiry anyway (`SessionManager`) and takes the connection with it.
 */
export const RECONNECT_DELAYS_MS: readonly number[] = [0, 2000, 5000, 10_000, 30_000];

const SLOW_RETRY_MS = RECONNECT_DELAYS_MS[RECONNECT_DELAYS_MS.length - 1] ?? 30_000;

/**
 * The one hub connection of a session. Owns the whole lifecycle: connect, reconnect with {@link RECONNECT_DELAYS_MS},
 * the connection state in the store, and what arrives - hints become cache tags, invalidated at once when nothing was
 * invalidated in the last second and otherwise together at the end of that second (`createTagCoalescer`: at most once
 * per second). Every transition to "connected", the first included, invalidates the realtime tags once, through the
 * same throttle: hints sent while there was no connection are lost.
 *
 * A token the hub refuses (401 on negotiate: revoked, or the security stamp changed) ends the session exactly like a
 * 401 of the API (`apiBaseQuery`): only when the token used is still the current one.
 *
 * @param after The previous connection has stopped: two connections never exist at once.
 * @returns Stops the connection; resolves when it is closed.
 */
export function startRealtime(
  store: AppStore,
  createHub: HubFactory,
  after: Promise<void> = Promise.resolve(),
): () => Promise<void> {
  let stopped = false;
  let hub: HubConnection | null = null;
  let timer: number | undefined;
  /** Attempts that failed since the connection was last established (or since the start). */
  let failures = 0;
  let wasConnected = false;

  const coalescer = createTagCoalescer((tags) => {
    store.dispatch(baseApi.util.invalidateTags(tags));
  });

  const setState = (state: ConnectionState) => {
    if (store.getState().realtime.connection !== state) {
      store.dispatch(connectionChanged(state));
    }
  };

  /** What the user is told while there is no connection: the quick retries first, then "offline". */
  const waitingState = (): ConnectionState =>
    failures >= RECONNECT_DELAYS_MS.length ? 'disconnected' : wasConnected ? 'reconnecting' : 'connecting';

  const retry = () => {
    const delay = RECONNECT_DELAYS_MS[failures] ?? SLOW_RETRY_MS;
    failures += 1;
    setState(waitingState());
    timer = window.setTimeout(() => void connect(), delay);
  };

  const connect = async () => {
    setState(waitingState());
    let tokenUsed: string | null = null;
    let current: HubConnection | null = null;
    try {
      current = await createHub({
        accessToken: () => (tokenUsed = selectAccessToken(store.getState())),
        onHints: (batch) => {
          if (!stopped) {
            coalescer.add(tagsForBatch(batch));
          }
        },
        onClose: () => {
          if (!stopped && hub === current) {
            hub = null;
            failures = 0;
            retry();
          }
        },
      });
      if (stopped) {
        return;
      }

      hub = current;
      await current.start();
      if (stopped) {
        return;
      }

      failures = 0;
      wasConnected = true;
      setState('connected');
      coalescer.add(RESYNC_TAGS);
    } catch (error) {
      if (stopped) {
        return;
      }

      hub = null;
      // Never two connections: what is left of the failed one is closed before the next attempt.
      await current?.stop().catch(() => undefined);
      if (stopped) {
        return;
      }

      const refused = error instanceof HubUnauthorizedError && tokenUsed !== null;
      if (refused && selectAccessToken(store.getState()) === tokenUsed) {
        // The session ends; this connection is stopped by whoever started it.
        store.dispatch(loggedOut());
        return;
      }

      retry();
    }
  };

  void after.then(() => {
    if (!stopped) {
      void connect();
    }
  });

  return () => {
    stopped = true;
    window.clearTimeout(timer);
    coalescer.cancel();
    setState('disconnected');
    const closing = hub;
    hub = null;
    return closing ? closing.stop().catch(() => undefined) : Promise.resolve();
  };
}
