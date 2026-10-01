import { render } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ConfigProvider } from 'antd';
import { createMemoryRouter } from 'react-router';
import { App } from '@/app/App';
import { routes } from '@/app/router';
import { createAppStore } from '@/app/store';
import { saveSession, type Session } from '@/features/auth/session';

interface RenderAppOptions {
  /** Initial URL (path + query). */
  route?: string;
  /** Stored session to start with (as after a page reload while signed in); signed out when omitted. */
  session?: Session;
}

/** Renders the whole application the way `main.tsx` does, with a memory router and a fresh store. */
export function renderApp({ route = '/', session }: RenderAppOptions = {}) {
  if (session) {
    saveSession(session);
  }

  const store = createAppStore();
  const router = createMemoryRouter(routes, { initialEntries: [route] });
  // Animations never finish in jsdom (no transition events), so closed dialogs and dropdowns would stay in the DOM.
  // The app's own ConfigProvider inherits this token; nothing else differs from production.
  const view = render(
    <ConfigProvider theme={{ token: { motion: false } }}>
      <App store={store} router={router} />
    </ConfigProvider>,
  );

  /** Current location as `path?query`. */
  const location = () => `${router.state.location.pathname}${router.state.location.search}`;

  // jsdom cannot compute AntD's CSS-in-JS styles reliably, so the pointer-events check of user-event is switched off.
  return { ...view, store, router, location, user: userEvent.setup({ pointerEventsCheck: 0 }) };
}
