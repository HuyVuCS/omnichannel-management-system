import { useEffect } from 'react';
import { RouterProvider } from 'react-router';
import { SessionManager } from '@/features/auth/SessionManager';
import { ApiErrorNotifier } from '@/shared/components/ApiErrorNotifier';
import { AppProviders } from './providers';
import type { AppRouter } from './router';
import type { AppStore } from './store';
import { trackTabVisibility } from './tabVisibility';

interface AppProps {
  store: AppStore;
  router: AppRouter;
}

/** Store and router are created by the caller (`main.tsx`, or a test with a memory router). */
export function App({ store, router }: AppProps) {
  // Polling queries pause while the tab is hidden; tied to the mounted application and removed with it.
  useEffect(() => trackTabVisibility(store), [store]);

  return (
    <AppProviders store={store}>
      <SessionManager />
      <ApiErrorNotifier />
      <RouterProvider router={router} />
    </AppProviders>
  );
}
