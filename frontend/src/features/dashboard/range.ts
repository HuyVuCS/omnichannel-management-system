import dayjs, { type Dayjs } from 'dayjs';
import { oneOf } from '@/shared/utils/address';
import { endOfLocalDay, readLocalDayRange, startOfLocalDay } from '@/shared/utils/dateRange';
import { API_DATE_FORMAT } from '@/shared/utils/format';
import type { DashboardSummaryArgs } from './dashboardApi';

/** `24h` is the server's default range and is never written to the address. */
export const RANGE_PRESETS = ['24h', 'today', '7d', '30d'] as const;
export type RangePreset = (typeof RANGE_PRESETS)[number];

export const DEFAULT_PRESET = '24h' satisfies RangePreset;

/** Address parameter of a preset; a custom range uses `from` / `to` (local days, as on the list pages). */
export const RANGE_PARAM = 'range';

export interface DashboardRange {
  /** The preset in use; undefined while a custom range is. */
  preset?: RangePreset;
  /** First and last local day of a custom range, as the address keeps them. */
  from?: string;
  to?: string;
  /** What the request carries. */
  request: DashboardSummaryArgs;
}

/** How many local days a preset covers, today included. */
const PRESET_DAYS: Record<Exclude<RangePreset, '24h'>, number> = { today: 1, '7d': 7, '30d': 30 };

/**
 * The range the address asks for. A custom range (`from` and/or `to`, real dates) wins over a preset; an unknown preset
 * is the default. Presets other than the default are whole local days ending today, sent as the local start of the first
 * day and the local end of today with the UTC offset; the default sends nothing - "the last 24 hours" is the server's.
 * Whether a range is allowed (its length) is the server's decision.
 */
export function readDashboardRange(searchParams: URLSearchParams, now: Dayjs = dayjs()): DashboardRange {
  const custom = readLocalDayRange(searchParams);
  if (custom.from || custom.to) {
    return {
      from: custom.from,
      to: custom.to,
      request: { from: custom.fromInstant, to: custom.toInstant },
    };
  }

  const preset = oneOf(RANGE_PRESETS, searchParams.get(RANGE_PARAM)) ?? DEFAULT_PRESET;
  if (preset === DEFAULT_PRESET) {
    return { preset, request: {} };
  }

  const today = now.format(API_DATE_FORMAT);
  const firstDay = now.subtract(PRESET_DAYS[preset] - 1, 'day').format(API_DATE_FORMAT);
  return { preset, request: { from: startOfLocalDay(firstDay), to: endOfLocalDay(today) } };
}
