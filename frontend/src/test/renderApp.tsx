import { render } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ConfigProvider } from 'antd';
import { StrictMode } from 'react';
import { createMemoryRouter } from 'react-router';
import { App } from '@/app/App';
import { routes } from '@/app/router';
import { createAppStore } from '@/app/store';
import { saveSession, type Session } from '@/features/auth/session';
import type { HubFactory } from '@/features/realtime/hub';
import { idleHub } from './hub';

interface RenderAppOptions {
  /** Initial URL (path + query). */
  route?: string;
  /** Stored session to start with (as after a page reload while signed in); signed out when omitted. */
  session?: Session;
  /**
   * Keep AntD animations on. They never finish in jsdom, so a closed dialog stays mounted — the situation of a dialog
   * that is reopened before its close animation has ended.
   */
  animations?: boolean;
  /**
   * The notifications hub. By default one that never gets connected, so pages behave as without realtime (the
   * order-entry outcome polls); realtime tests pass `fakeHub().create`.
   */
  createHub?: HubFactory;
  /** Render inside React's StrictMode like `main.tsx` (every effect runs twice in development). */
  strict?: boolean;
}

/** Renders the whole application the way `main.tsx` does, with a memory router and a fresh store. */
export function renderApp({
  route = '/',
  session,
  animations = false,
  createHub = idleHub,
  strict = false,
}: RenderAppOptions = {}) {
  if (session) {
    saveSession(session);
  }

  const store = createAppStore();
  const router = createMemoryRouter(routes, { initialEntries: [route] });
  // Animations never finish in jsdom (no transition events), so closed dialogs and dropdowns would stay in the DOM.
  // The app's own ConfigProvider inherits this token; nothing else differs from production.
  const app = (
    <ConfigProvider theme={{ token: { motion: animations } }}>
      <App store={store} router={router} createHub={createHub} />
    </ConfigProvider>
  );
  const view = render(strict ? <StrictMode>{app}</StrictMode> : app);

  /** Current location as `path?query`. */
  const location = () => `${router.state.location.pathname}${router.state.location.search}`;

  // jsdom cannot compute AntD's CSS-in-JS styles reliably, so the pointer-events check of user-event is switched off.
  return { ...view, store, router, location, user: userEvent.setup({ pointerEventsCheck: 0 }) };
}
