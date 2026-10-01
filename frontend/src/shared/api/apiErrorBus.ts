import { isRejectedWithValue, type Middleware } from '@reduxjs/toolkit';
import { isApiError, isUnexpectedError, type ApiError } from './types';

type Listener = (error: ApiError) => void;

const listeners = new Set<Listener>();

/**
 * Unexpected API errors (network failure, 5xx) of any endpoint. The UI subscribes once (ApiErrorNotifier) and shows a
 * notification with the correlation id; pages only handle the errors that are theirs (validation, conflicts).
 */
export const apiErrorBus = {
  subscribe(listener: Listener): () => void {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  },
  emit(error: ApiError): void {
    listeners.forEach((listener) => listener(error));
  },
};

/** Forwards every rejected RTK Query call that failed for an unexpected reason to the bus. */
export const apiErrorMiddleware: Middleware = () => (next) => (action) => {
  if (isRejectedWithValue(action) && isApiError(action.payload) && isUnexpectedError(action.payload)) {
    apiErrorBus.emit(action.payload);
  }

  return next(action);
};
