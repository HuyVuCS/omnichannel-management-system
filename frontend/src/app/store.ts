import { combineReducers, configureStore } from '@reduxjs/toolkit';
import { authReducer } from '@/features/auth/authSlice';
import { loadSession, saveSession } from '@/features/auth/session';
import { forgetImageLinks } from '@/features/products/imageLinks';
import { realtimeReducer } from '@/features/realtime/realtimeSlice';
import { apiErrorMiddleware } from '@/shared/api/apiErrorBus';
import { baseApi } from '@/shared/api/baseApi';

const rootReducer = combineReducers({
  auth: authReducer,
  realtime: realtimeReducer,
  [baseApi.reducerPath]: baseApi.reducer,
});

export type RootState = ReturnType<typeof rootReducer>;

/**
 * Redux holds client state only (the session, the state of the realtime connection); server state is RTK Query's
 * cache. The session is hydrated from
 * sessionStorage here and written back whenever it changes; when it ends, the API cache is dropped so the next user
 * never sees the previous user's data.
 */
export function createAppStore() {
  const store = configureStore({
    reducer: rootReducer,
    preloadedState: { auth: { session: loadSession(), signedOutByUser: false } },
    middleware: (getDefaultMiddleware) => getDefaultMiddleware().concat(baseApi.middleware, apiErrorMiddleware),
  });

  let previous = store.getState().auth.session;
  store.subscribe(() => {
    const current = store.getState().auth.session;
    if (current === previous) {
      return;
    }

    const ended = previous !== null && current === null;
    previous = current;
    saveSession(current);
    if (ended) {
      store.dispatch(baseApi.util.resetApiState());
      // Image links are credentials of the storage for a few minutes: the next user gets their own.
      forgetImageLinks();
    }
  });

  return store;
}

export type AppStore = ReturnType<typeof createAppStore>;
export type AppDispatch = AppStore['dispatch'];
