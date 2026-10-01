/**
 * A value taken from the address when it is one of the allowed values, otherwise nothing: filters in the address that
 * are not valid (an unknown status, a typo) are ignored and never sent to the API.
 */
export function oneOf<T extends string>(values: readonly T[], raw: string | null): T | undefined {
  return (values as readonly string[]).includes(raw ?? '') ? (raw as T) : undefined;
}
