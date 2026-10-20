import type { FormInstance } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import type { TFunction } from 'i18next';
import { applyProblemToForm } from '@/shared/api/problemDetails';
import { isApiError } from '@/shared/api/types';

/**
 * The longest range of an orders export, in local days. The API decides (`Reports:MaxRangeDays`, default 366); this
 * copy only answers before a request is sent. A deployment that changes the server's limit changes this constant too
 * (docs/operations-guide.md "Reports and Excel export") - a lower server limit costs one request that comes back with a field error.
 */
export const MAX_EXPORT_RANGE_DAYS = 366;

/** The range an orders export starts with: this many days, today included. */
export const DEFAULT_RANGE_DAYS = 30;

/**
 * The IANA name of the browser's time zone (`Asia/Ho_Chi_Minh`): the times in the file are written in it, because that
 * is the zone the same times are shown in on screen. `UTC` when the browser does not say.
 */
export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

/** The last {@link DEFAULT_RANGE_DAYS} local days, ending today. */
export function defaultRange(today: Dayjs = dayjs()): [Dayjs, Dayjs] {
  const last = today.startOf('day');
  return [last.subtract(DEFAULT_RANGE_DAYS - 1, 'day'), last];
}

/** How many local days a range covers, both ends included. */
export function rangeDays(range: readonly [Dayjs, Dayjs]): number {
  return range[1].startOf('day').diff(range[0].startOf('day'), 'day') + 1;
}

/**
 * Shows the API's answer to an export request on its form, like every form does (`applyProblemToForm`): a validation
 * error goes to its field, anything else the user can act on is returned for the message above the form -
 * `export_too_large`, `too_many_exports`, and a validation error of something that is no field (`timeZone`).
 * `fieldOf` names the form field of an API field where the two differ (`from` and `to` are one range picker).
 */
export function applyExportProblem<Values>(
  form: FormInstance<Values>,
  error: unknown,
  t: TFunction,
  fieldOf: Record<string, string> = {},
): string[] {
  if (!isApiError(error) || !error.problem?.errors) {
    return applyProblemToForm(form, error, t);
  }

  const errors: Record<string, string[]> = {};
  for (const [key, messages] of Object.entries(error.problem.errors)) {
    const field = fieldOf[key] ?? key;
    errors[field] = [...(errors[field] ?? []), ...messages];
  }

  return applyProblemToForm(form, { ...error, problem: { ...error.problem, errors } }, t);
}
