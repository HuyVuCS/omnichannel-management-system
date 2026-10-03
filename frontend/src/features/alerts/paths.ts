export const ALERTS_PATH = '/alerts';

/**
 * The API lets Manager and Admin acknowledge an alert; everyone signed in may read alerts. Kept next to the paths (not
 * in alertsApi), like the roles of the stock import and of the manual retry.
 */
export const ACKNOWLEDGE_ROLES = ['Admin', 'Manager'] as const;

/** Address parameters of the alert list's filters that take several values: one entry per value, as the API reads them. */
export const STATUS_PARAM = 'status';
export const TYPE_PARAM = 'type';

/** Statuses and types of the alert list as the address keeps them (`?status=Open&status=Resolved&type=LowStock`). */
export interface AlertListFilter {
  status?: readonly string[];
  type?: readonly string[];
}

/** The alert list with filters, e.g. from a number of the dashboard. Without a status the list shows the server's default. */
export function alertsListPath({ status = [], type = [] }: AlertListFilter = {}): string {
  const params = new URLSearchParams();
  for (const value of status) {
    params.append(STATUS_PARAM, value);
  }

  for (const value of type) {
    params.append(TYPE_PARAM, value);
  }

  const query = params.toString();
  return query ? `${ALERTS_PATH}?${query}` : ALERTS_PATH;
}
