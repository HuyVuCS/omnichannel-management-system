import { useEffect } from 'react';
import { RouterProvider } from 'react-router';
import { SessionManager } from '@/features/auth/SessionManager';
import type { HubFactory } from '@/features/realtime/hub';
import { RealtimeManager } from '@/features/realtime/RealtimeManager';
import { ApiErrorNotifier } from '@/shared/components/ApiErrorNotifier';
import { AppProviders } from './providers';
import type { AppRouter } from './router';
import type { AppStore } from './store';
import { trackTabVisibility } from './tabVisibility';

interface AppProps {
  store: AppStore;
  router: AppRouter;
  /** Creates the connection to the notifications hub (`main.tsx`: SignalR; a test: a fake hub). */
  createHub: HubFactory;
}

/** Store, router and hub are created by the caller (`main.tsx`, or a test with a memory router and a fake hub). */
export function App({ store, router, createHub }: AppProps) {
  // Polling queries pause while the tab is hidden; tied to the mounted application and removed with it.
  useEffect(() => trackTabVisibility(store), [store]);

  return (
    <AppProviders store={store}>
      <SessionManager />
      <RealtimeManager createHub={createHub} />
      <ApiErrorNotifier />
      <RouterProvider router={router} />
    </AppProviders>
  );
}
