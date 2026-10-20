/** RFC 7807 body as the API returns it (see docs/operations-guide.md "API conventions"). */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  /** Stable, machine-readable code, e.g. `user_email_exists`; translated on the client (`errors.<errorCode>`). */
  errorCode?: string;
  correlationId?: string;
  /** Validation errors: camelCase JSON path of the field (`items[0].stock`) → messages in the request language. */
  errors?: Record<string, string[]>;
}

/** Transport-level failures reported by RTK Query's fetchBaseQuery instead of an HTTP status. */
export type TransportErrorStatus = 'FETCH_ERROR' | 'PARSING_ERROR' | 'TIMEOUT_ERROR' | 'CUSTOM_ERROR';

/** The one error shape every endpoint rejects with. */
export interface ApiError {
  status: number | TransportErrorStatus;
  /** Present when the server answered with a problem document. */
  problem?: ProblemDetails;
  /** The server's correlation id, or the one this client sent when there was no usable response. */
  correlationId: string;
}

/** Paging contract shared by every list endpoint. */
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export function isApiError(value: unknown): value is ApiError {
  return (
    typeof value === 'object' &&
    value !== null &&
    'status' in value &&
    'correlationId' in value &&
    typeof (value as ApiError).correlationId === 'string'
  );
}

/** Network failures and 5xx: nothing the user did wrong; reported globally with the correlation id. */
export function isUnexpectedError(error: ApiError): boolean {
  return typeof error.status === 'string' || error.status >= 500;
}
