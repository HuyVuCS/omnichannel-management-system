import dayjs from 'dayjs';

const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
const SUFFIX_LENGTH = 6;

/** The largest multiple of the alphabet size that fits into a byte: bytes from here on are skipped, so every character is equally likely. */
const UNBIASED_LIMIT = 256 - (256 % ALPHABET.length);

/**
 * A new external order id for the order-entry page: `{prefix}-{local date YYYYMMDD}-{six random characters A-Z0-9}`,
 * e.g. `WEB-20261001-7KQ2ZD`. The id is the idempotency key of the order on its channel, so it is generated once per
 * order and kept until the order was accepted; it stands in for the number a real channel would send.
 */
export function generateExternalOrderId(prefix: string, now: Date = new Date()): string {
  let suffix = '';
  while (suffix.length < SUFFIX_LENGTH) {
    for (const byte of crypto.getRandomValues(new Uint8Array(SUFFIX_LENGTH * 2))) {
      if (byte < UNBIASED_LIMIT && suffix.length < SUFFIX_LENGTH) {
        suffix += ALPHABET[byte % ALPHABET.length];
      }
    }
  }

  return `${prefix}-${dayjs(now).format('YYYYMMDD')}-${suffix}`;
}
