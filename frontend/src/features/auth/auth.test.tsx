import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { PagedResult } from '@/shared/api/types';
import type { User } from '@/features/users/usersApi';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, problem, server, sessionOf, staffUser } from '@/test/server';
import { SESSION_STORAGE_KEY, type Session } from './session';

const emptyUserPage: PagedResult<User> = { items: [], page: 1, pageSize: 20, totalCount: 0 };

function storedSession(): Session | null {
  const raw = window.sessionStorage.getItem(SESSION_STORAGE_KEY);
  return raw === null ? null : (JSON.parse(raw) as Session);
}

/** Login endpoint answering with a session of `user`; records the submitted bodies. */
function loginAs(user = adminUser) {
  const bodies: unknown[] = [];
  const session = sessionOf(user);
  server.use(
    http.post(api('/auth/login'), async ({ request }) => {
      bodies.push(await request.json());
      return HttpResponse.json({ ...session, tokenType: 'Bearer' });
    }),
  );
  return { bodies, session };
}

async function submitLogin(user: ReturnType<typeof renderApp>['user'], email: string, password: string) {
  await user.type(await screen.findByLabelText('E-mail'), email);
  await user.type(screen.getByLabelText('Mật khẩu'), password);
  await user.click(screen.getByRole('button', { name: 'Đăng nhập' }));
}

describe('login', () => {
  it('stores the session in sessionStorage and lands on the dashboard', async () => {
    const { bodies, session } = loginAs(adminUser);
    const { user, location } = renderApp({ route: '/login' });

    await submitLogin(user, 'admin@example.com', 'Correct-passw0rd');

    expect(await screen.findByRole('heading', { name: 'Tổng quan' })).toBeInTheDocument();
    expect(location()).toBe('/');
    expect(bodies).toEqual([{ email: 'admin@example.com', password: 'Correct-passw0rd' }]);
    expect(storedSession()).toEqual(session);
    // Header shows who is signed in.
    expect(screen.getByTestId('app-user')).toHaveTextContent('admin@example.com');
    expect(screen.getByTestId('app-user')).toHaveTextContent('Quản trị');
  });

  it('shows one generic error for a wrong password and stays signed out', async () => {
    server.use(http.post(api('/auth/login'), () => problem(401, { title: 'Invalid credentials' })));
    const { user, location } = renderApp({ route: '/login' });

    await submitLogin(user, 'admin@example.com', 'wrong');

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'E-mail hoặc mật khẩu không đúng, hoặc tài khoản đang tạm khoá.',
    );
    expect(location()).toBe('/login');
    expect(storedSession()).toBeNull();
  });

  it('tells the user to wait when the login rate limit answers 429', async () => {
    // The rate limiter rejects without a body.
    server.use(http.post(api('/auth/login'), () => new HttpResponse(null, { status: 429 })));
    const { user } = renderApp({ route: '/login' });

    await submitLogin(user, 'admin@example.com', 'whatever');

    expect(await screen.findByRole('alert')).toHaveTextContent('Đăng nhập quá nhiều lần');
    expect(storedSession()).toBeNull();
  });

  it('shows validation errors of the API on the fields', async () => {
    server.use(
      http.post(api('/auth/login'), () =>
        problem(400, { errorCode: 'validation_failed', errors: { email: ["'email' không hợp lệ (máy chủ)."] } }),
      ),
    );
    const { user } = renderApp({ route: '/login' });

    await submitLogin(user, 'admin@example.com', 'whatever');

    expect(await screen.findByText("'email' không hợp lệ (máy chủ).")).toBeInTheDocument();
  });
});

describe('route guards', () => {
  it('sends an unauthenticated visitor of /users to the login page and back after login', async () => {
    loginAs(adminUser);
    server.use(http.get(api('/users'), () => HttpResponse.json(emptyUserPage)));
    const { user, location, router } = renderApp({ route: '/users' });

    await screen.findByLabelText('E-mail');
    expect(location()).toBe('/login?returnUrl=%2Fusers');
    expect(new URLSearchParams(router.state.location.search).get('returnUrl')).toBe('/users');

    await submitLogin(user, 'admin@example.com', 'Correct-passw0rd');

    expect(await screen.findByRole('heading', { name: 'Người dùng' })).toBeInTheDocument();
    expect(location()).toBe('/users');
  });

  it('never redirects to another site after login', async () => {
    loginAs(adminUser);
    const { user, location } = renderApp({ route: '/login?returnUrl=%2F%2Fevil.example%2Fphish' });

    await submitLogin(user, 'admin@example.com', 'Correct-passw0rd');

    expect(await screen.findByRole('heading', { name: 'Tổng quan' })).toBeInTheDocument();
    expect(location()).toBe('/');
  });

  it('hides the Users menu item from Staff and answers /users with the 403 page', async () => {
    // No /api/users handler: the page must not even ask the API.
    const { router } = renderApp({ route: '/', session: sessionOf(staffUser) });

    expect(await screen.findByRole('link', { name: 'Sản phẩm' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Người dùng' })).not.toBeInTheDocument();

    await router.navigate('/users');

    expect(await screen.findByText('Không có quyền truy cập')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Người dùng' })).not.toBeInTheDocument();
  });

  it('shows the Users menu item to an Admin', async () => {
    renderApp({ route: '/', session: sessionOf(adminUser) });

    expect(await screen.findByRole('link', { name: 'Người dùng' })).toBeInTheDocument();
  });

  it('renders a localised placeholder for pages of later tasks and a 404 page for unknown addresses', async () => {
    const { router } = renderApp({ route: '/inventory', session: sessionOf(staffUser) });

    expect(await screen.findByText('Trang này sẽ có ở task 012.')).toBeInTheDocument();

    await router.navigate('/no-such-page');
    expect(await screen.findByText('Không tìm thấy trang')).toBeInTheDocument();
  });
});

describe('session end', () => {
  it('logs out and returns to the login page when the API answers 401', async () => {
    server.use(http.get(api('/users'), () => problem(401)));
    const { location } = renderApp({ route: '/users', session: sessionOf(adminUser) });

    await screen.findByLabelText('E-mail');
    expect(location()).toBe('/login?returnUrl=%2Fusers');
    expect(storedSession()).toBeNull();
  });

  it('logs out when the restored session is no longer valid on the server (revoked token)', async () => {
    server.use(http.get(api('/auth/me'), () => problem(401)));
    const { location } = renderApp({ route: '/', session: sessionOf(adminUser) });

    await screen.findByLabelText('E-mail');
    expect(location()).toBe('/login');
    expect(storedSession()).toBeNull();
  });

  it('refreshes e-mail and roles from the server on start', async () => {
    // The stored session says Staff; the store says the user is an Admin by now.
    server.use(http.get(api('/auth/me'), () => HttpResponse.json({ ...staffUser, roles: ['Admin'] })));
    renderApp({ route: '/', session: sessionOf(staffUser) });

    expect(await screen.findByRole('link', { name: 'Người dùng' })).toBeInTheDocument();
    expect(storedSession()?.user.roles).toEqual(['Admin']);
  });

  it('ignores a stored session that has already expired', async () => {
    const { location } = renderApp({ route: '/', session: sessionOf(adminUser, -1000) });

    await screen.findByLabelText('E-mail');
    expect(location()).toBe('/login');
    expect(storedSession()).toBeNull();
  });

  it('logs out when the access token expires while the page is open', async () => {
    const { location } = renderApp({ route: '/inventory', session: sessionOf(staffUser, 400) });

    expect(await screen.findByText('Trang này sẽ có ở task 012.')).toBeInTheDocument();

    await waitFor(() => expect(location()).toBe('/login?returnUrl=%2Finventory'), { timeout: 3000 });
    expect(storedSession()).toBeNull();
  });

  it('goes to the plain login page on logout', async () => {
    const { user, location } = renderApp({ route: '/inventory', session: sessionOf(staffUser) });

    await user.click(await screen.findByRole('button', { name: 'Đăng xuất' }));

    await screen.findByLabelText('E-mail');
    expect(location()).toBe('/login');
    expect(storedSession()).toBeNull();
  });
});
