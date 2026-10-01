import {
  createApi,
  fetchBaseQuery,
  type BaseQueryFn,
  type FetchArgs,
  type FetchBaseQueryError,
  type FetchBaseQueryMeta,
} from '@reduxjs/toolkit/query/react';
import { loggedOut, selectAccessToken, type StateWithAuth } from '@/features/auth/authSlice';
import { currentLanguage } from '@/shared/i18n';
import type { ApiError, ProblemDetails } from './types';

export const CORRELATION_HEADER = 'X-Correlation-Id';

/**
 * Same origin as the page, path `/api` (the dev server proxies it to the API). Absolute because fetch outside a browser
 * (tests) cannot resolve relative URLs.
 */
function apiBaseUrl(): string {
  return new URL('/api', window.location.origin).href;
}

function newCorrelationId(): string {
  if (typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  // randomUUID needs a secure context; plain http on a LAN address does not have one.
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
}

const rawBaseQuery = fetchBaseQuery({
  baseUrl: apiBaseUrl(),
  prepareHeaders: (headers, { getState }) => {
    const token = selectAccessToken(getState() as StateWithAuth);
    if (token) {
      headers.set('Authorization', `Bearer ${token}`);
    }

    // Validation messages come back in the UI language (API request localisation).
    headers.set('Accept-Language', currentLanguage());
    return headers;
  },
});

function isProblemDetails(data: unknown): data is ProblemDetails {
  return typeof data === 'object' && data !== null && !Array.isArray(data);
}

function toApiError(error: FetchBaseQueryError, sentCorrelationId: string): ApiError {
  if (typeof error.status === 'number') {
    const problem = isProblemDetails(error.data) ? error.data : undefined;
    return { status: error.status, problem, correlationId: problem?.correlationId ?? sentCorrelationId };
  }

  // A non-JSON error body (e.g. 429 from the rate limiter, 502 from a proxy) arrives as PARSING_ERROR with the real status.
  if (error.status === 'PARSING_ERROR') {
    return { status: error.originalStatus, correlationId: sentCorrelationId };
  }

  return { status: error.status, correlationId: sentCorrelationId };
}

/**
 * Base query of every endpoint: bearer token, `Accept-Language`, a fresh `X-Correlation-Id` per request, errors
 * normalised to {@link ApiError}, and logout on a 401 answer to an authenticated call (expired or revoked token).
 * The redirect to the login page follows from the cleared session (see RequireAuth).
 */
export const apiBaseQuery: BaseQueryFn<string | FetchArgs, unknown, ApiError, object, FetchBaseQueryMeta> = async (
  args,
  api,
  extraOptions,
) => {
  const correlationId = newCorrelationId();
  const request: FetchArgs = typeof args === 'string' ? { url: args } : { ...args };
  request.headers = { ...(request.headers as Record<string, string> | undefined), [CORRELATION_HEADER]: correlationId };

  const tokenUsed = selectAccessToken(api.getState() as StateWithAuth);
  const result = await rawBaseQuery(request, api, extraOptions);
  if (result.error === undefined) {
    return { data: result.data, meta: result.meta };
  }

  const error = toApiError(result.error, correlationId);
  if (error.status === 401 && tokenUsed !== null && selectAccessToken(api.getState() as StateWithAuth) === tokenUsed) {
    api.dispatch(loggedOut());
  }

  return { error, meta: result.meta };
};

/**
 * The single RTK Query API. Features add their endpoints with `baseApi.injectEndpoints` and use the tag of their
 * resource for invalidation; server state lives here, never in Redux slices.
 */
export const baseApi = createApi({
  reducerPath: 'api',
  baseQuery: apiBaseQuery,
  tagTypes: ['User', 'Product', 'Customer', 'Channel', 'Inventory', 'InventoryMovement'],
  endpoints: () => ({}),
});
