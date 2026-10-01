import { setupListeners } from '@reduxjs/toolkit/query';
import { baseApi } from '@/shared/api/baseApi';
import type { AppStore } from './store';

function tabIsVisible(): boolean {
  return document.visibilityState !== 'hidden';
}

/**
 * Keeps RTK Query's "focused" flag equal to the visibility of the tab, so a query that polls with
 * `skipPollingIfUnfocused` (the outcome of a submitted order) pauses while the tab is hidden and resumes when it is
 * shown again. Nothing else follows from it: no query sets `refetchOnFocus` or `refetchOnReconnect`, and the
 * online/offline listeners of RTK's default handler are not registered at all.
 *
 * `setupListeners` with its default handler is not used for two reasons:
 * - the flag is part of the API state, and `resetApiState` (session end, see `store.ts`) puts it back to what it was
 *   when the page was loaded - wrong for a tab that was opened in the background. The store subscription below
 *   corrects it whenever it differs from the real visibility;
 * - the default handler registers one set of listeners per page load, for the first store only (tests create a store
 *   per test).
 *
 * @returns Stops tracking (the application was unmounted).
 */
export function trackTabVisibility(store: AppStore): () => void {
  return setupListeners(store.dispatch, (dispatch, { onFocus, onFocusLost }) => {
    const sync = () => {
      const visible = tabIsVisible();
      if (store.getState()[baseApi.reducerPath].config.focused !== visible) {
        dispatch(visible ? onFocus() : onFocusLost());
      }
    };

    document.addEventListener('visibilitychange', sync);
    const unsubscribe = store.subscribe(sync);
    sync();

    return () => {
      document.removeEventListener('visibilitychange', sync);
      unsubscribe();
    };
  });
}
