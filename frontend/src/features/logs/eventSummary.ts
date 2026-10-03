import type { TFunction } from 'i18next';
import { ALERT_TYPES } from '@/features/alerts/alertsApi';
import { ROLES } from '@/features/auth/session';
import { TASK_TYPES } from '@/features/processing/processingApi';
import { formatNumber } from '@/shared/utils/format';
import { isKnownEventType, type KnownEventType, type SystemEvent } from './systemEventsApi';

/** What a summary shows for a field the details do not have. */
const EMPTY = '—';

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
      return { text: t(key, { messageId: text(details.messageId) }) };

    case 'stock_sync.message_rejected':
      return { text: t(key, { messageId: text(details.messageId), queue: text(details.queue) }) };

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
