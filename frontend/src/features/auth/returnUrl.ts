export const LOGIN_PATH = '/login';
export const RETURN_URL_PARAM = 'returnUrl';

/**
 * Where to go after login. Only paths inside this application are accepted: an absolute or protocol-relative URL in the
 * query string must never send a freshly signed-in user to another site (open redirect).
 */
export function safeReturnUrl(value: string | null): string {
  if (
    !value ||
    !value.startsWith('/') ||
    value.startsWith('//') ||
    value.startsWith('/\\') ||
    value.startsWith(LOGIN_PATH)
  ) {
    return '/';
  }

  return value;
}

/** `/login`, plus `?returnUrl=…` for every page except the dashboard. */
export function loginLocation(from: { pathname: string; search: string }): { pathname: string; search: string } {
  const target = `${from.pathname}${from.search}`;
  const search = target === '/' ? '' : `?${new URLSearchParams({ [RETURN_URL_PARAM]: target }).toString()}`;
  return { pathname: LOGIN_PATH, search };
}
