import dayjs from 'dayjs';

/**
 * Formatting is the same in both UI languages on purpose (switching language changes text only): money is always VND
 * with Vietnamese grouping, quantities use the same grouping, date-times are always `DD/MM/YYYY HH:mm` in the browser's
 * time zone.
 */
const moneyFormat = new Intl.NumberFormat('vi-VN', { style: 'currency', currency: 'VND', maximumFractionDigits: 0 });
const numberFormat = new Intl.NumberFormat('vi-VN');

export const DATE_TIME_FORMAT = 'DD/MM/YYYY HH:mm';
export const DATE_FORMAT = 'DD/MM/YYYY';

/** Date-only values as the API sends and expects them (`DateOnly`). */
export const API_DATE_FORMAT = 'YYYY-MM-DD';

/** `1234567` → `1.234.567 ₫`. */
export function formatMoney(amount: number): string {
  return moneyFormat.format(amount);
}

/** A quantity: `1234567` → `1.234.567`. */
export function formatNumber(value: number): string {
  return numberFormat.format(value);
}

const sizeFormat = new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 1 });
const KIB = 1024;
const MIB = 1024 * 1024;

/** The size of a file in KB below one megabyte, in MB from there on, with one decimal: `4300` → `4,2 KB`. */
export function formatFileSize(bytes: number): string {
  return bytes >= MIB ? `${sizeFormat.format(bytes / MIB)} MB` : `${sizeFormat.format(bytes / KIB)} KB`;
}

/** ISO 8601 (as the API sends it, UTC) → local `DD/MM/YYYY HH:mm`; empty for missing or invalid values. */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) {
    return '';
  }

  const date = dayjs(value);
  return date.isValid() ? date.format(DATE_TIME_FORMAT) : '';
}

/** A date without time (`YYYY-MM-DD`, e.g. an expiry date) → `DD/MM/YYYY`; empty for missing or invalid values. */
export function formatDate(value: string | null | undefined): string {
  if (!value) {
    return '';
  }

  const date = dayjs(value);
  return date.isValid() ? date.format(DATE_FORMAT) : '';
}
