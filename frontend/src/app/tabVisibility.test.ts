import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it } from 'vitest';
import { channelsApi } from '@/features/channels/channelsApi';
import { baseApi } from '@/shared/api/baseApi';
import { api, server } from '@/test/server';
import { createAppStore } from './store';
import { trackTabVisibility } from './tabVisibility';

/** What a browser does when the tab is hidden or shown. */
function setTabVisibility(state: 'visible' | 'hidden') {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
  document.dispatchEvent(new Event('visibilitychange'));
}

const focused = (store: ReturnType<typeof createAppStore>) => store.getState().api.config.focused;

describe('tab visibility tracking', () => {
  afterEach(() => {
    // Back to jsdom's own value.
    delete (document as { visibilityState?: unknown }).visibilityState;
  });

  it('keeps the "focused" flag of the API state equal to the visibility of the tab', () => {
    const store = createAppStore();
    const stop = trackTabVisibility(store);
    expect(focused(store)).toBe(true);

    setTabVisibility('hidden');
    expect(focused(store)).toBe(false);

    setTabVisibility('visible');
    expect(focused(store)).toBe(true);
    stop();
  });

  it('corrects the flag after the API state was reset (session end) while the tab is hidden', () => {
    const store = createAppStore();
    const stop = trackTabVisibility(store);
    setTabVisibility('hidden');

    // A reset puts the flag back to what it was when the page was loaded - not to what the tab is now.
    store.dispatch(baseApi.util.resetApiState());

    expect(focused(store)).toBe(false);
    stop();
  });

  it('starts with the real visibility and stops following when the application is unmounted', () => {
    setTabVisibility('hidden');
    const store = createAppStore();
    expect(focused(store)).toBe(true);

    const stop = trackTabVisibility(store);
    expect(focused(store)).toBe(false);

    stop();
    setTabVisibility('visible');
    expect(focused(store)).toBe(false);
  });

  it('changes nothing else: a cached query is not fetched again when the tab is shown again', async () => {
    let requests = 0;
    server.use(
      http.get(api('/channels'), () => {
        requests += 1;
        return HttpResponse.json([]);
      }),
    );
    const store = createAppStore();
    const stop = trackTabVisibility(store);
    const subscription = store.dispatch(channelsApi.endpoints.listChannels.initiate());
    await subscription;
    expect(requests).toBe(1);

    setTabVisibility('hidden');
    setTabVisibility('visible');
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(requests).toBe(1);
    expect(store.getState().api.config).toMatchObject({ refetchOnFocus: false, refetchOnReconnect: false });
    subscription.unsubscribe();
    stop();
  });
});
