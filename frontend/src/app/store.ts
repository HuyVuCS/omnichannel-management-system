import { combineReducers, configureStore } from '@reduxjs/toolkit';
import { authReducer } from '@/features/auth/authSlice';
import { loadSession, saveSession } from '@/features/auth/session';
import { apiErrorMiddleware } from '@/shared/api/apiErrorBus';
import { baseApi } from '@/shared/api/baseApi';

const rootReducer = combineReducers({
  auth: authReducer,
  [baseApi.reducerPath]: baseApi.reducer,
});

export type RootState = ReturnType<typeof rootReducer>;

/**
 * Redux holds client state only (the session); server state is RTK Query's cache. The session is hydrated from
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
    }
  });

  return store;
}

export type AppStore = ReturnType<typeof createAppStore>;
export type AppDispatch = AppStore['dispatch'];
