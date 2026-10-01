import dayjs from 'dayjs';
import { API_DATE_FORMAT } from './format';

/**
 * A date filter is a range of local days. The address keeps the days (`from=YYYY-MM-DD&to=YYYY-MM-DD`, shareable and
 * independent of the time zone it is opened in); the request carries the instants: the local start of the first day
 * and the local end of the last day, as ISO 8601 with the UTC offset (the API's range is inclusive).
 */
export interface LocalDayRange {
  /** First day as the address keeps it, when it is a real date. */
  from?: string;
  /** Last day as the address keeps it, when it is a real date. */
  to?: string;
  /** `from` for the request: local start of that day. */
  fromInstant?: string;
  /** `to` for the request: local end of that day. */
  toInstant?: string;
}

/** A calendar date as the address keeps it (`YYYY-MM-DD`); anything else, including impossible dates, is ignored. */
export function parseLocalDate(raw: string | null | undefined): string | undefined {
  return raw && dayjs(raw).isValid() && dayjs(raw).format(API_DATE_FORMAT) === raw ? raw : undefined;
}

/** Local start of a day: `2026-10-01` → `2026-10-01T00:00:00+07:00`. */
export function startOfLocalDay(day: string): string {
  return dayjs(day).startOf('day').format('YYYY-MM-DDTHH:mm:ssZ');
}

/** Local end of a day: `2026-10-01` → `2026-10-01T23:59:59.999+07:00`. */
export function endOfLocalDay(day: string): string {
  return dayjs(day).endOf('day').format('YYYY-MM-DDTHH:mm:ss.SSSZ');
}

/** The `from` / `to` days of the address and what to send for them; values that are no dates are left out. */
export function readLocalDayRange(searchParams: URLSearchParams): LocalDayRange {
  const from = parseLocalDate(searchParams.get('from'));
  const to = parseLocalDate(searchParams.get('to'));
  return {
    from,
    to,
    fromInstant: from ? startOfLocalDay(from) : undefined,
    toInstant: to ? endOfLocalDay(to) : undefined,
  };
}
