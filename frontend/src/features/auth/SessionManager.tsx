import { Modal } from 'antd';
import { useEffect } from 'react';
import { useStore } from 'react-redux';
import type { AppStore } from '@/app/store';
import { useAppDispatch, useAppSelector } from '@/shared/hooks/store';
import { authApi } from './authApi';
import { loggedOut, selectSession, userRefreshed } from './authSlice';

/** setTimeout cannot wait longer than this (2^31 - 1 ms, about 24.8 days). */
const MAX_TIMER_MS = 2_147_483_647;

/**
 * Session upkeep, mounted once:
 * - on start, a session restored from sessionStorage is checked against `GET /api/auth/me` (a revoked token answers
 *   401, which signs the user out; otherwise e-mail and roles are refreshed from the store);
 * - when the access token expires the user is signed out (there are no refresh tokens);
 * - when the session ends (logout, expiry, a 401 from the API) every open confirmation dialog is closed. They are
 *   rendered by AntD's `App` above the router, so they would otherwise stay over the login page. `Modal.destroyAll()`
 *   also closes the dialogs opened through `App.useApp().modal` (AntD registers both kinds in the same list). Form
 *   dialogs belong to their page and go away with it.
 */
export function SessionManager() {
  const dispatch = useAppDispatch();
  const store = useStore() as AppStore;
  const expiresAt = useAppSelector((state) => selectSession(state)?.expiresAt ?? null);
  const signedIn = expiresAt !== null;

  useEffect(() => {
    if (!signedIn) {
      Modal.destroyAll();
    }
  }, [signedIn]);

  useEffect(() => {
    if (selectSession(store.getState()) === null) {
      return;
    }

    const request = dispatch(authApi.endpoints.me.initiate(undefined, { forceRefetch: true }));
    request
      .unwrap()
      .then((user) => dispatch(userRefreshed(user)))
      .catch(() => {
        // 401 has already signed the user out (base query); network errors and 5xx are reported globally.
      });

    return () => request.unsubscribe();
    // Once per page load: this is the "is the restored session still valid" check, not a per-login check.
  }, [dispatch, store]);

  useEffect(() => {
    if (expiresAt === null) {
      return;
    }

    let timer: number | undefined;
    const check = () => {
      const remaining = Date.parse(expiresAt) - Date.now();
      if (Number.isNaN(remaining) || remaining <= 0) {
        dispatch(loggedOut());
      } else {
        timer = window.setTimeout(check, Math.min(remaining, MAX_TIMER_MS));
      }
    };

    check();
    return () => window.clearTimeout(timer);
  }, [dispatch, expiresAt]);

  return null;
}
