import dayjs from 'dayjs';

/**
 * Formatting is the same in both UI languages on purpose (switching language changes text only): money is always VND
 * with Vietnamese grouping, date-times always `DD/MM/YYYY HH:mm` in the browser's time zone.
 */
const moneyFormat = new Intl.NumberFormat('vi-VN', { style: 'currency', currency: 'VND', maximumFractionDigits: 0 });

export const DATE_TIME_FORMAT = 'DD/MM/YYYY HH:mm';

/** `1234567` → `1.234.567 ₫`. */
export function formatMoney(amount: number): string {
  return moneyFormat.format(amount);
}

/** ISO 8601 (as the API sends it, UTC) → local `DD/MM/YYYY HH:mm`; empty for missing or invalid values. */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) {
    return '';
  }

  const date = dayjs(value);
  return date.isValid() ? date.format(DATE_TIME_FORMAT) : '';
}
