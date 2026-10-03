import dayjs, { type Dayjs } from 'dayjs';
import {
  DEFAULT_PRESET,
  presetLocalDays,
  RANGE_PARAM,
  RANGE_PRESETS,
  type RangePreset,
} from '@/features/dashboard/range';
import { oneOf } from '@/shared/utils/address';
import { readLocalDayRange } from '@/shared/utils/dateRange';
import { SINCE_PARAM } from './paths';

/** An instant as the address keeps it: UTC, whole seconds (`2026-10-02T01:05:09Z`). */
const SINCE_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/;

export interface LogsRange {
  /** The preset in use; undefined while a custom range is. */
  preset?: RangePreset;
  /** First and last local day of a custom range, as the address keeps them. */
  from?: string;
  to?: string;
  /**
   * What the request carries. Undefined while the address does not say which window is meant: the page then writes the
   * default to the address and asks nothing before that.
   */
  request?: { from?: string; to?: string };
}

/** The start of "the last 24 hours" counted from `now`, as the address keeps it. */
export function newSince(now: Dayjs = dayjs()): string {
  return now
    .subtract(24, 'hour')
    .toDate()
    .toISOString()
    .replace(/\.\d{3}Z$/, 'Z');
}

function parseSince(raw: string | null): string | undefined {
  return raw && SINCE_PATTERN.test(raw) && dayjs(raw).isValid() ? raw : undefined;
}

/**
 * The time window the address asks for - the same presets as the dashboard, with one difference: this list has no
 * default range on the server, so "the last 24 hours" must be a fixed instant, or every render would ask for another
 * window. It is kept in the address (`?since=…`, written by the page when the address names no window): a reload and a
 * shared link show the same window, and events that arrive later are part of it ("Refresh" shows them).
 *
 * A custom range (`from` and/or `to`, real dates) wins over a preset; the other presets (`?range=today|7d|30d`) are
 * whole local days ending today. Whether a range is allowed (its length) is the server's decision.
 */
export function readLogsRange(searchParams: URLSearchParams, now: Dayjs = dayjs()): LogsRange {
  const custom = readLocalDayRange(searchParams);
  if (custom.from || custom.to) {
    return { from: custom.from, to: custom.to, request: { from: custom.fromInstant, to: custom.toInstant } };
  }

  const preset = oneOf(RANGE_PRESETS, searchParams.get(RANGE_PARAM)) ?? DEFAULT_PRESET;
  if (preset !== DEFAULT_PRESET) {
    return { preset, request: presetLocalDays(preset, now) };
  }

  const since = parseSince(searchParams.get(SINCE_PARAM));
  return { preset, request: since ? { from: since } : undefined };
}
