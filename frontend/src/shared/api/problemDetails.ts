import type { FormInstance } from 'antd';
import type { TFunction } from 'i18next';
import i18n from '@/shared/i18n';
import { isApiError, isUnexpectedError, type ApiError } from './types';

/** A field path as AntD forms use it: `items[0].stock` → `['items', 0, 'stock']`. */
export type FieldPath = (string | number)[];

/**
 * Parses an error key of a ValidationProblemDetails (camelCase JSON path, the API convention) into a field path.
 * `items[0].stock` → `['items', 0, 'stock']`, `password` → `['password']`.
 */
export function parseFieldPath(key: string): FieldPath {
  const path: FieldPath = [];
  for (const segment of key.split('.')) {
    const match = /^([^[\]]*)((?:\[\d+\])*)$/.exec(segment);
    if (!match) {
      path.push(segment);
      continue;
    }

    const [, name, indexes] = match;
    if (name) {
      path.push(name);
    }

    for (const index of indexes?.matchAll(/\[(\d+)\]/g) ?? []) {
      path.push(Number(index[1]));
    }
  }

  return path;
}

/**
 * The message for an API error in the UI language: `errors.<errorCode>` when the code is known, otherwise a message
 * for the HTTP status, otherwise a generic one. Server texts (`detail`) are not shown: they are English only.
 */
export function translateApiError(t: TFunction, error: unknown): string {
  if (!isApiError(error)) {
    return t('errors.generic');
  }

  const code = error.problem?.errorCode;
  if (code && code !== 'validation_failed' && i18n.exists(`errors.${code}`)) {
    return t(`errors.${code}`);
  }

  switch (error.status) {
    case 'FETCH_ERROR':
    case 'TIMEOUT_ERROR':
      return t('errors.network');
    case 400:
      return t('errors.validation_failed');
    case 401:
      return t('errors.unauthorized');
    case 403:
      return t('errors.forbidden');
    case 404:
      return t('errors.notFound');
    case 409:
      return t('errors.conflict');
    case 429:
      return t('errors.tooManyRequests');
    default:
      return typeof error.status === 'number' && error.status < 500 ? t('errors.generic') : t('errors.server');
  }
}

export interface ApplyProblemOptions {
  /** Error codes that belong to one field, e.g. `{ user_email_exists: 'email' }`. */
  codeFields?: Record<string, string>;
}

/**
 * Shows an API error on a form:
 * - validation errors go to their fields (`errors` keys are JSON paths; messages are already localised by the API);
 * - an `errorCode` listed in `codeFields` goes to that field, translated;
 * - everything else that is the user's to fix (4xx) is returned for a form-level alert, including validation errors
 *   whose key matches no field of the form.
 * Network errors and 5xx return nothing: they are reported globally with the correlation id.
 */
export function applyProblemToForm<Values>(
  form: FormInstance<Values>,
  error: unknown,
  t: TFunction,
  options: ApplyProblemOptions = {},
): string[] {
  if (!isApiError(error) || isUnexpectedError(error)) {
    return [];
  }

  const registered = new Set(form.getFieldsError().map((field) => JSON.stringify(field.name)));
  const isRegistered = (path: FieldPath) => registered.has(JSON.stringify(path));
  const fieldErrors: { name: FieldPath; errors: string[] }[] = [];
  const formErrors: string[] = [];

  const validation = (error as ApiError).problem?.errors;
  if (validation && Object.keys(validation).length > 0) {
    for (const [key, messages] of Object.entries(validation)) {
      const path = parseFieldPath(key);
      if (isRegistered(path)) {
        fieldErrors.push({ name: path, errors: messages });
      } else {
        formErrors.push(...messages);
      }
    }
  } else {
    const code = error.problem?.errorCode;
    const field = code ? options.codeFields?.[code] : undefined;
    if (field && isRegistered([field])) {
      fieldErrors.push({ name: [field], errors: [translateApiError(t, error)] });
    } else {
      formErrors.push(translateApiError(t, error));
    }
  }

  if (fieldErrors.length > 0) {
    // FormInstance.setFields is typed against the form's value shape; paths coming from the server are plain arrays.
    form.setFields(fieldErrors as Parameters<typeof form.setFields>[0]);
  }

  return formErrors;
}
