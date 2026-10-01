import { describe, expect, it } from 'vitest';
import { endOfLocalDay, parseLocalDate, readLocalDayRange, startOfLocalDay } from './dateRange';

describe('local day range', () => {
  it('accepts only real calendar dates written as YYYY-MM-DD', () => {
    expect(parseLocalDate('2026-10-01')).toBe('2026-10-01');
    expect(parseLocalDate('2024-02-29')).toBe('2024-02-29');

    for (const raw of ['2026-13-45', '2026-02-30', '2026-2-3', '01/10/2026', 'yesterday', '', null, undefined]) {
      expect(parseLocalDate(raw), String(raw)).toBeUndefined();
    }
  });

  it('turns a day into the local start and the local end of that day, written with the UTC offset', () => {
    const start = startOfLocalDay('2026-09-01');
    expect(start).toMatch(/^2026-09-01T00:00:00[+-]\d{2}:\d{2}$/);
    expect(new Date(start).getTime()).toBe(new Date(2026, 8, 1, 0, 0, 0, 0).getTime());

    const end = endOfLocalDay('2026-09-30');
    expect(end).toMatch(/^2026-09-30T23:59:59\.999[+-]\d{2}:\d{2}$/);
    expect(new Date(end).getTime()).toBe(new Date(2026, 8, 30, 23, 59, 59, 999).getTime());
  });

  it('reads from/to of an address and leaves out what is not a date', () => {
    expect(readLocalDayRange(new URLSearchParams('from=2026-09-01&to=2026-09-30'))).toEqual({
      from: '2026-09-01',
      to: '2026-09-30',
      fromInstant: startOfLocalDay('2026-09-01'),
      toInstant: endOfLocalDay('2026-09-30'),
    });
    expect(readLocalDayRange(new URLSearchParams('from=2026-13-45&to=2026-09-30'))).toEqual({
      from: undefined,
      to: '2026-09-30',
      fromInstant: undefined,
      toInstant: endOfLocalDay('2026-09-30'),
    });
    expect(readLocalDayRange(new URLSearchParams(''))).toEqual({
      from: undefined,
      to: undefined,
      fromInstant: undefined,
      toInstant: undefined,
    });
  });
});
