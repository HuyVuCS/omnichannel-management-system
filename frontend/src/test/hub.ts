import { act } from '@testing-library/react';
import {
  HubUnauthorizedError,
  type HintBatch,
  type HintEvent,
  type HubConnection,
  type HubFactory,
  type HubOptions,
} from '@/features/realtime/hub';

/** A hub that never gets connected: the application behaves as it does without realtime. */
export const idleHub: HubFactory = () =>
  Promise.resolve({
    start: () => new Promise<void>(() => {}),
    stop: () => Promise.resolve(),
  });

/** One connection the application created through the fake hub. */
export interface FakeConnection {
  /** The access token the application gave when the connection was started. */
  token: string | null;
  started: boolean;
  stopped: boolean;
  /** The connection is established (the application's `start()` resolves). */
  accept(): Promise<void>;
  /** The connection cannot be established: the server is unreachable. */
  fail(): Promise<void>;
  /** The hub refuses the access token (401 on negotiate). */
  refuse(): Promise<void>;
  /** An established connection is lost. */
  drop(): Promise<void>;
  /** The hub's `hints` message. */
  send(batch: Partial<HintBatch> | HintEvent[]): Promise<void>;
}

/**
 * The notifications hub of a test, in place of the SignalR client (`signalrHub.ts`): every connection the application
 * creates is recorded, and the test decides what happens to it. With `autoAccept` every connection is established as
 * soon as it is started.
 */
export function fakeHub({ autoAccept = false }: { autoAccept?: boolean } = {}) {
  const connections: FakeConnection[] = [];
  /** How many connections were started and not stopped at the same time, at most. */
  const usage = { open: 0, maxOpen: 0 };

  const create: HubFactory = (options: HubOptions) => {
    let settle: { resolve: () => void; reject: (error: unknown) => void } | undefined;
    let open = false;
    const close = () => {
      if (open) {
        open = false;
        usage.open -= 1;
      }
    };
    /** Lets the application's reaction (promise callbacks, store updates, renders) finish. */
    const settled = (action: () => void) =>
      act(async () => {
        action();
        await Promise.resolve();
      });

    const connection: FakeConnection = {
      token: null,
      started: false,
      stopped: false,
      accept: () => settled(() => settle?.resolve()),
      fail: () =>
        settled(() => {
          close();
          settle?.reject(new Error('The hub is unreachable.'));
        }),
      refuse: () =>
        settled(() => {
          close();
          settle?.reject(new HubUnauthorizedError());
        }),
      drop: () =>
        settled(() => {
          close();
          options.onClose();
        }),
      send: (batch) =>
        settled(() => options.onHints(Array.isArray(batch) ? { events: batch, resync: false } : (batch as HintBatch))),
    };

    const hub: HubConnection = {
      start() {
        connection.started = true;
        connection.token = options.accessToken();
        open = true;
        usage.open += 1;
        usage.maxOpen = Math.max(usage.maxOpen, usage.open);
        return new Promise<void>((resolve, reject) => {
          settle = { resolve, reject };
          if (autoAccept) {
            resolve();
          }
        });
      },
      stop() {
        connection.stopped = true;
        close();
        settle?.reject(new Error('The connection was stopped.'));
        return Promise.resolve();
      },
    };

    connections.push(connection);
    return Promise.resolve(hub);
  };

  return {
    create,
    connections,
    usage,
    /** The connection created last. */
    latest(): FakeConnection {
      const connection = connections.at(-1);
      if (!connection) {
        throw new Error('The application has not created a hub connection.');
      }

      return connection;
    },
  };
}
