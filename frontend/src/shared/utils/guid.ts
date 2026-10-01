const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** True for an id in the form the API uses (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`). Ids taken from the address are checked before they are sent. */
export function isGuid(value: string | null | undefined): value is string {
  return typeof value === 'string' && GUID_PATTERN.test(value);
}
