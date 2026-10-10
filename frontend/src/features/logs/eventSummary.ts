import type { TFunction } from 'i18next';
import { ALERT_TYPES } from '@/features/alerts/alertsApi';
import { ROLES } from '@/features/auth/session';
import { TASK_TYPES } from '@/features/processing/processingApi';
import { REPORT_TYPES } from '@/features/reports/reportsApi';
import { formatNumber } from '@/shared/utils/format';
import { isKnownEventType, type KnownEventType, type SystemEvent } from './systemEventsApi';

/** What a summary shows for a field the details do not have. */
const EMPTY = '—';

/**
 * The `reasonCode`s of a rejected message this client has a label for (`logs.rejection.*`; the API's
 * `MessageRejectionCodes`). A code that is not listed is shown as it is, a row without one (written before the code
 * existed) as "—".
 */
export const REJECTION_CODES = [
  'invalid_json',
  'empty_body',
  'unexpected_event_type',
  'missing_task_id',
  'missing_message_ids',
  'message_ids_too_long',
  'payload_not_object',
  'invalid_payload',
  'empty_payload',
  'task_id_mismatch',
  'missing_order_fields',
  'no_items',
  'invalid_item',
  'duplicate_product',
  'missing_product_id',
  'missing_sync_target',
  'unknown_task',
  'task_mismatch',
  'task_of_other_channel',
] as const;

/**
 * The error codes of a channel call this client has a label for (`logs.channelErrors.*`; the API's
 * `ChannelApiErrorCodes`, and `unexpected`: what a reconciliation run reports for a channel whose read failed with
 * anything else). A code that is not listed is shown as it is.
 */
export const CHANNEL_ERROR_CODES = [
  'channel_timeout',
  'channel_unavailable',
  'channel_rate_limited',
  'channel_rejected',
  'channel_circuit_open',
  'unexpected',
] as const;

/** Between the parts of a summary that is put together from several texts. */
const PART_SEPARATOR = ' · ';

export interface EventSummary {
  /** One line that says what happened. */
  text: string;
  /** More of the same event that does not fit the line (the error message of a failure). */
  more?: string;
}

/** The translation key of an event type: its name with `_` for the dots (`inventory.imported` → `inventory_imported`). */
export function eventTypeKey(eventType: KnownEventType): string {
  return eventType.replaceAll('.', '_');
}

/** The label of an event type; a type this client does not know is shown as it is. */
export function eventTypeLabel(t: TFunction, eventType: string): string {
  return isKnownEventType(eventType) ? t(`logs.eventTypes.${eventTypeKey(eventType)}`) : eventType;
}

function text(value: unknown): string {
  return typeof value === 'string' && value !== '' ? value : EMPTY;
}

function count(value: unknown): string {
  return typeof value === 'number' && Number.isFinite(value) ? formatNumber(value) : EMPTY;
}

/** The label of an enum value the API sends as text; a value this client does not know is shown as it is. */
function label(t: TFunction, prefix: string, known: readonly string[], value: unknown): string {
  return typeof value === 'string' && known.includes(value) ? t(`${prefix}.${value}`) : text(value);
}

/** The lines of a rejected order (`[{ productId, requested, available }]`). */
function shortLines(value: unknown): { requested: unknown; available: unknown }[] {
  return Array.isArray(value)
    ? value.filter(
        (line): line is { requested: unknown; available: unknown } => typeof line === 'object' && line !== null,
      )
    : [];
}

/** The channels a reconciliation run could not read (`[{ channel, errorCode, pairs }]`). */
function unreadableChannels(value: unknown): { channel: unknown; errorCode: unknown }[] {
  return Array.isArray(value)
    ? value.filter(
        (entry): entry is { channel: unknown; errorCode: unknown } => typeof entry === 'object' && entry !== null,
      )
    : [];
}

/**
 * One line per event type, built from the details the API returned (and from nothing else: the texts are templates,
 * the values are the API's). No summary for an event without details or of a type this client does not know.
 */
export function eventSummary(t: TFunction, event: SystemEvent): EventSummary | null {
  const details = event.details;
  if (!details || !isKnownEventType(event.eventType)) {
    return null;
  }

  const key = `logs.summary.${eventTypeKey(event.eventType)}`;
  switch (event.eventType) {
    case 'inventory.imported':
      return {
        text: t(key, {
          referenceId: text(details.referenceId),
          itemCount: count(details.itemCount),
          totalQuantity: count(details.totalQuantity),
        }),
      };

    case 'order.insufficient_stock': {
      const lines = shortLines(details.items);
      if (lines.length === 0) {
        return null;
      }

      return lines.length === 1
        ? { text: t(`${key}.one`, { requested: count(lines[0]!.requested), available: count(lines[0]!.available) }) }
        : { text: t(`${key}.many`, { products: formatNumber(lines.length) }) };
    }

    case 'order.already_exists':
      return { text: t(key, { externalOrderId: text(details.externalOrderId) }) };

    case 'order.message_rejected':
      return {
        text: t(key, {
          messageId: text(details.messageId),
          reason: label(t, 'logs.rejection', REJECTION_CODES, details.reasonCode),
        }),
      };

    case 'stock_sync.message_rejected':
      return {
        text: t(key, {
          messageId: text(details.messageId),
          queue: text(details.queue),
          reason: label(t, 'logs.rejection', REJECTION_CODES, details.reasonCode),
        }),
      };

    case 'processing.failed':
      return {
        text: t(key, {
          taskType: label(t, 'processing.taskTypes', TASK_TYPES, details.taskType),
          attemptNo: count(details.attemptNo),
          errorCode: text(details.errorCode),
        }),
        // Sanitised by the API, like the message of an attempt.
        more:
          typeof details.errorMessage === 'string' && details.errorMessage !== '' ? details.errorMessage : undefined,
      };

    case 'processing.retry_requested':
      return { text: t(key, { taskType: label(t, 'processing.taskTypes', TASK_TYPES, details.taskType) }) };

    case 'outbox.publish_failed':
      return { text: t(key, { eventType: text(details.eventType), publishAttempts: count(details.publishAttempts) }) };

    case 'alert.acknowledged':
      return { text: t(key, { type: label(t, 'alerts.types', ALERT_TYPES, details.type) }) };

    // Who asked is the actor of the event (the "who" column): the API resolves it from `requestedBy`.
    case 'report.export_requested':
      return { text: t(key, { reportType: label(t, 'reports.types', REPORT_TYPES, details.reportType) }) };

    case 'report_export.message_rejected':
      return {
        text: t(key, {
          messageId: text(details.messageId),
          reason: label(t, 'logs.rejection', REJECTION_CODES, details.reasonCode),
        }),
      };

    // What the run found and did; the channels it could not read by name, with the reason.
    case 'reconciliation.completed': {
      const parts = [
        t(`${key}.drift`, { drifted: count(details.drifted), checked: count(details.checked) }),
        t(`${key}.tasks`, { tasksCreated: count(details.tasksCreated) }),
      ];
      // Of those tasks, the ones for products that do not differ: their last sync had failed and is repeated.
      if (typeof details.failedRetried === 'number' && details.failedRetried > 0) {
        parts.push(t(`${key}.failedRetried`, { failedRetried: count(details.failedRetried) }));
      }

      if (typeof details.deferred === 'number' && details.deferred > 0) {
        parts.push(t(`${key}.deferred`, { deferred: count(details.deferred) }));
      }

      const channels = unreadableChannels(details.unreadable).map((entry) =>
        t(`${key}.channel`, {
          channel: text(entry.channel),
          reason: label(t, 'logs.channelErrors', CHANNEL_ERROR_CODES, entry.errorCode),
        }),
      );
      if (channels.length > 0) {
        parts.push(t(`${key}.unreadable`, { channels: channels.join(t(`${key}.channelSeparator`)) }));
      }

      return { text: parts.join(PART_SEPARATOR) };
    }

    case 'user.created':
      return { text: t(key, { role: label(t, 'roles', ROLES, details.role) }) };

    case 'user.role_changed':
      return {
        text: t(key, { from: label(t, 'roles', ROLES, details.from), to: label(t, 'roles', ROLES, details.to) }),
      };

    case 'user.locked':
    case 'user.unlocked':
    case 'user.password_reset':
      return { text: t(key) };

    default:
      return null;
  }
}
