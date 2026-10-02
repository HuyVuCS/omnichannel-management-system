import { act, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import { CONNECTION_INDICATOR_WIDTH } from '@/shared/components/layout';
import { fakeHub } from '@/test/hub';
import { expectLanguageSwitchChangesTextOnly } from '@/test/interactions';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, managerUser, server, sessionOf, staffUser } from '@/test/server';
import { FLUSH_WINDOW_MS } from './hintTags';
import { connectionChanged } from './realtimeSlice';

const emptyPage = { items: [], page: 1, pageSize: 20, totalCount: 0 };
const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
const indicator = () => screen.getByTestId('connection-indicator');

/** `POST /api/auth/login` answering with a session of `user`. */
function loginAs(user: AuthUser) {
  const session = sessionOf(user);
  server.use(http.post(api('/auth/login'), () => HttpResponse.json({ ...session, tokenType: 'Bearer' })));
  return session;
}

async function submitLogin(user: ReturnType<typeof renderApp>['user']) {
  await user.type(await screen.findByLabelText('E-mail'), 'someone@example.com');
  await user.type(screen.getByLabelText('Mật khẩu'), 'a-password');
  await user.click(screen.getByRole('button', { name: 'Đăng nhập' }));
}

/** A placeholder page: the shell without any request of its own. */
const QUIET_PAGE = '/alerts';

describe('realtime connection lifecycle', () => {
  it('connects only after sign-in, with the token of the session; logout closes it; a new login is a new connection', async () => {
    const hub = fakeHub();
    const { user } = renderApp({ route: `/login?returnUrl=${encodeURIComponent(QUIET_PAGE)}`, createHub: hub.create });
    await screen.findByLabelText('E-mail');
    await pause(100);
    // No session: nothing is created, the library is not even loaded.
    expect(hub.connections).toHaveLength(0);

    const first = loginAs(staffUser);
    await submitLogin(user);
    await waitFor(() => expect(hub.connections).toHaveLength(1));
    expect(hub.latest().token).toBe(first.accessToken);
    await hub.latest().accept();
    await waitFor(() => expect(indicator()).toHaveTextContent('Trực tiếp'));

    await user.click(screen.getByRole('button', { name: 'Đăng xuất' }));
    await screen.findByLabelText('E-mail');
    expect(hub.connections[0]?.stopped).toBe(true);
    await pause(100);
    expect(hub.connections).toHaveLength(1);

    const second = loginAs(managerUser);
    await submitLogin(user);
    await waitFor(() => expect(hub.connections).toHaveLength(2));
    expect(hub.latest().token).toBe(second.accessToken);
    expect(hub.latest().stopped).toBe(false);
    // The first one was closed before the second one was opened.
    expect(hub.usage.maxOpen).toBe(1);
    // A new session starts "connecting", not with the state the previous one ended in.
    await waitFor(() => expect(indicator()).toHaveAttribute('data-state', 'connecting'));
  });

  it('closes the connection when the access token expires', async () => {
    const hub = fakeHub({ autoAccept: true });
    renderApp({ route: QUIET_PAGE, session: sessionOf(staffUser, 1500), createHub: hub.create });
    await waitFor(() => expect(indicator()).toHaveTextContent('Trực tiếp'));
    expect(hub.latest().stopped).toBe(false);

    await screen.findByLabelText('E-mail');
    expect(hub.latest().stopped).toBe(true);
    await pause(100);
    expect(hub.connections).toHaveLength(1);
  });

  it('opens exactly one connection under StrictMode', async () => {
    const hub = fakeHub({ autoAccept: true });
    renderApp({ route: QUIET_PAGE, session: sessionOf(staffUser), createHub: hub.create, strict: true });
    await waitFor(() => expect(indicator()).toHaveTextContent('Trực tiếp'));
    await pause(100);

    expect(hub.connections).toHaveLength(1);
    expect(hub.usage.maxOpen).toBe(1);
    expect(hub.latest().stopped).toBe(false);
  });

  it('ends the session when the hub refuses the token, like a 401 of the API', async () => {
    const hub = fakeHub();
    const { location } = renderApp({ route: QUIET_PAGE, session: sessionOf(staffUser), createHub: hub.create });
    await waitFor(() => expect(hub.connections).toHaveLength(1));

    await hub.latest().refuse();

    await screen.findByLabelText('E-mail');
    // The session ended by itself: the user comes back to the page after signing in again.
    expect(location()).toBe(`/login?returnUrl=${encodeURIComponent(QUIET_PAGE)}`);
    await pause(100);
    expect(hub.connections).toHaveLength(1);
  });
});

describe('connection indicator', () => {
  it('says connecting, live, reconnecting and offline, with icon, text and a fixed width', async () => {
    const hub = fakeHub();
    const { store, user } = renderApp({ route: QUIET_PAGE, session: sessionOf(adminUser), createHub: hub.create });
    await waitFor(() => expect(hub.connections).toHaveLength(1));

    expect(indicator()).toHaveTextContent('Đang kết nối');
    expect(indicator()).toHaveStyle({ width: `${CONNECTION_INDICATOR_WIDTH}px` });
    expect(indicator().querySelector('[role="img"]')).toBeInTheDocument();
    const node = indicator();

    await hub.latest().accept();
    expect(indicator()).toHaveTextContent('Trực tiếp');
    await user.hover(indicator());
    expect(await screen.findByText('Dữ liệu trên màn hình được cập nhật tự động.')).toBeInTheDocument();
    await user.unhover(indicator());

    await hub.latest().drop();
    await waitFor(() => expect(indicator()).toHaveTextContent('Đang kết nối lại'));
    await user.hover(indicator());
    expect(
      await screen.findByText('Mất kết nối cập nhật trực tiếp, đang kết nối lại; dữ liệu có thể chưa mới nhất.'),
    ).toBeInTheDocument();
    await user.unhover(indicator());

    // Offline is reached after the quick retries (about 17 seconds, see realtimeConnection.test.ts).
    act(() => {
      store.dispatch(connectionChanged('disconnected'));
    });
    expect(indicator()).toHaveTextContent('Mất kết nối');
    await user.hover(indicator());
    expect(
      await screen.findByText('Mất kết nối cập nhật trực tiếp; dữ liệu có thể đã cũ. Hệ thống vẫn tự thử kết nối lại.'),
    ).toBeInTheDocument();

    // The same node in every state, always as wide.
    expect(indicator()).toBe(node);
    expect(indicator()).toHaveStyle({ width: `${CONNECTION_INDICATOR_WIDTH}px` });
    expect(indicator().querySelector('[role="img"]')).toBeInTheDocument();
  });

  it('keeps its state and width when the language changes', async () => {
    const hub = fakeHub({ autoAccept: true });
    const { user, location } = renderApp({ route: QUIET_PAGE, session: sessionOf(staffUser), createHub: hub.create });
    await waitFor(() => expect(indicator()).toHaveTextContent('Trực tiếp'));
    const node = indicator();
    const style = node.getAttribute('style');

    await expectLanguageSwitchChangesTextOnly(user, location, {
      vietnamese: ['Trực tiếp'],
      english: ['Live'],
      requestCount: () => hub.connections.length,
    });

    expect(indicator()).toBe(node);
    expect(indicator().getAttribute('style')).toBe(style);
    expect(indicator()).toHaveAttribute('data-state', 'connected');
    expect(hub.latest().stopped).toBe(false);
  });
});

describe('hints and the cache', () => {
  it('refetches a list on screen at most once per second during a burst and removes a cached list nobody shows', async () => {
    let orderRequests = 0;
    server.use(
      http.get(api('/orders'), () => {
        orderRequests += 1;
        return HttpResponse.json(emptyPage);
      }),
      http.get(api('/channels'), () => HttpResponse.json([])),
      http.get(api('/customers'), () => HttpResponse.json(emptyPage)),
    );
    const hub = fakeHub({ autoAccept: true });
    const { store, user } = renderApp({ route: '/orders', session: sessionOf(staffUser), createHub: hub.create });
    await screen.findByRole('heading', { name: 'Đơn hàng' });
    await waitFor(() => expect(indicator()).toHaveTextContent('Trực tiếp'));

    // The page's own request and, when the page was there before the connection, the one every new connection causes
    // (hints before it were missed). That invalidation goes out the moment the hub connects: a page that is loaded
    // later has nothing to fetch again.
    await pause(FLUSH_WINDOW_MS + 300);
    const loaded = orderRequests;
    expect(loaded).toBeGreaterThanOrEqual(1);
    expect(loaded).toBeLessThanOrEqual(2);

    // Idle: the first hub message refetches at once.
    await hub.latest().send([{ type: 'order.created', orderId: 'o1' }]);
    await waitFor(() => expect(orderRequests).toBe(loaded + 1));

    // Three more within the second of that refetch: one refetch, when the second is over.
    await hub.latest().send([{ type: 'order.created', orderId: 'o2' }]);
    await hub.latest().send([{ type: 'order.created', orderId: 'o3' }]);
    await hub.latest().send([{ type: 'processing.task.changed', taskId: 't1', orderId: 'o1' }]);
    await pause(200);
    expect(orderRequests).toBe(loaded + 1);
    await pause(FLUSH_WINDOW_MS + 300);
    expect(orderRequests).toBe(loaded + 2);

    // A hint about something that is not shown anywhere asks for nothing.
    await hub.latest().send([{ type: 'unknown.event', all: true }]);
    await pause(FLUSH_WINDOW_MS + 300);
    expect(orderRequests).toBe(loaded + 2);

    // The list is left: its entry stays in the cache for a while, without a subscriber.
    const cachedOrderLists = () =>
      Object.keys(store.getState().api.queries).filter((key) => key.startsWith('listOrders'));
    await user.click(screen.getByRole('link', { name: 'Kênh bán' }));
    await screen.findByRole('heading', { name: 'Kênh bán' });
    expect(cachedOrderLists()).toHaveLength(1);

    // RTK Query removes an invalidated entry nobody is subscribed to instead of refetching it: it cannot be shown
    // stale later, and nothing is requested for a page that is not open.
    await hub.latest().send([{ type: 'order.created', orderId: 'o4' }]);
    await waitFor(() => expect(cachedOrderLists()).toHaveLength(0));
    await pause(FLUSH_WINDOW_MS + 300);
    expect(orderRequests).toBe(loaded + 2);
  });
});
