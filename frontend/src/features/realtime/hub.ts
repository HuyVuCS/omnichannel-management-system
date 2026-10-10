/**
 * What the application needs from the notifications hub (`/api/hubs/notifications`, README "Realtime hints and
 * dashboard summary"), behind a small interface: the SignalR client lives in `signalrHub.ts` and is loaded only after
 * sign-in; tests use a fake hub and never touch the library.
 */

/** One hint of the hub's `hints` message: what kind of thing changed and which ids - never data. */
export interface HintEvent {
  /**
   * `order.created`, `order.changed`, `inventory.changed`, `processing.task.changed`, `alert.changed`, `low_stock.created`; types this
   * client does not know are ignored.
   */
  type: string;
  /** More changed than is listed: everything of this type is stale. */
  all?: boolean;
  orderId?: string;
  taskId?: string;
  alertId?: string;
  alertType?: string;
  channelId?: string;
  productId?: string;
  productIds?: string[];
  taskType?: string;
  status?: string;
}

export interface HintBatch {
  events: HintEvent[];
  /** Hints may have been missed: everything on screen is stale. */
  resync: boolean;
}

/** The hub refused the access token (negotiate answered 401): revoked, or the security stamp changed. */
export class HubUnauthorizedError extends Error {
  constructor() {
    super('The notifications hub refused the access token.');
    this.name = 'HubUnauthorizedError';
  }
}

export interface HubConnection {
  /**
   * Connects once. Rejects with {@link HubUnauthorizedError} when the hub refused the token and with any other error
   * when the connection could not be established; a connection is never retried by the hub itself.
   */
  start(): Promise<void>;
  /** Closes the connection (or gives up a start that is still running). `onClose` is not called for it. */
  stop(): Promise<void>;
}

export interface HubOptions {
  /** The current access token, asked for at every connect. */
  accessToken: () => string | null;
  onHints: (batch: HintBatch) => void;
  /** An established connection was lost. */
  onClose: () => void;
}

/** Creates one connection (not started). Asynchronous because the real one loads its library first. */
export type HubFactory = (options: HubOptions) => Promise<HubConnection>;
