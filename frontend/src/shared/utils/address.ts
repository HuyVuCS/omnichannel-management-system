/**
 * A value taken from the address when it is one of the allowed values, otherwise nothing: filters in the address that
 * are not valid (an unknown status, a typo) are ignored and never sent to the API.
 */
export function oneOf<T extends string>(values: readonly T[], raw: string | null): T | undefined {
  return (values as readonly string[]).includes(raw ?? '') ? (raw as T) : undefined;
}

/**
 * The same for a filter that takes several values (`?status=Open&status=Resolved`): the allowed ones among the values
 * in the address, each once, in the order of `values` - so the same choice always makes the same request.
 */
export function someOf<T extends string>(values: readonly T[], raw: readonly string[]): T[] {
  return values.filter((value) => raw.includes(value));
}
