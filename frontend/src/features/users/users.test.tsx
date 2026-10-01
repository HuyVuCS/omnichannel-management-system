import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { LANGUAGE_STORAGE_KEY, readStoredLanguage } from '@/shared/i18n';
import type { PagedResult } from '@/shared/api/types';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, problem, server, sessionOf } from '@/test/server';
import type { User } from './usersApi';

type AppUser = ReturnType<typeof renderApp>['user'];

function user(overrides: Partial<User> & Pick<User, 'userId' | 'email'>): User {
  return { role: 'Staff', isLocked: false, lockoutEnd: null, accessFailedCount: 0, ...overrides };
}

const alice = user({ userId: 'u-alice', email: 'alice@example.com', role: 'Manager', accessFailedCount: 2 });
const bob = user({ userId: 'u-bob', email: 'bob@example.com' });
const carol = user({
  userId: 'u-carol',
  email: 'carol@example.com',
  isLocked: true,
  lockoutEnd: '9999-12-31T23:59:59.9999999+00:00',
});

/**
 * `GET /api/users` backed by a list the test can change; records the query string, `Accept-Language` and
 * correlation id of every request.
 */
function serveUsers(initial: User[], totalCount = initial.length) {
  const state = { users: initial, totalCount };
  const requests: { query: Record<string, string>; language: string | null; correlationId: string | null }[] = [];
  server.use(
    http.get(api('/users'), ({ request }) => {
      const url = new URL(request.url);
      requests.push({
        query: Object.fromEntries(url.searchParams),
        language: request.headers.get('Accept-Language'),
        correlationId: request.headers.get('X-Correlation-Id'),
      });
      const page: PagedResult<User> = {
        items: state.users,
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 20),
        totalCount: state.totalCount,
      };
      return HttpResponse.json(page);
    }),
  );
  return { state, requests };
}

function renderUsers(route = '/users') {
  return renderApp({ route, session: sessionOf(adminUser) });
}

function rowOf(email: string): HTMLElement {
  const row = screen.getByText(email).closest('tr');
  if (!row) {
    throw new Error(`No table row for ${email}`);
  }

  return row;
}

/** Opens an AntD Select (found by its accessible name) and picks the option with the given text. */
async function chooseOption(appUser: AppUser, select: HTMLElement, optionText: string) {
  await appUser.click(select);
  const options = await screen.findAllByText(optionText);
  const option = options.find((element) => element.classList.contains('ant-select-item-option-content'));
  if (!option) {
    throw new Error(`No option "${optionText}"`);
  }

  await appUser.click(option);
}

async function openCreateDialog(appUser: AppUser) {
  await appUser.click(await screen.findByRole('button', { name: /Thêm người dùng/ }));
  return screen.findByRole('dialog');
}

describe('users page', () => {
  it('renders one page of users with role, status and failed sign-ins, and asks the API for page 1', async () => {
    const { requests } = serveUsers([alice, bob, carol], 45);
    renderUsers();

    expect(await screen.findByText('alice@example.com')).toBeInTheDocument();
    expect(within(rowOf('alice@example.com')).getByText('Quản lý')).toBeInTheDocument();
    expect(within(rowOf('alice@example.com')).getByText('Hoạt động')).toBeInTheDocument();
    expect(within(rowOf('alice@example.com')).getByText('2')).toBeInTheDocument();
    expect(within(rowOf('carol@example.com')).getByText('Đã khoá')).toBeInTheDocument();
    expect(within(rowOf('carol@example.com')).getByRole('button', { name: 'Mở khoá' })).toBeInTheDocument();
    expect(within(rowOf('bob@example.com')).getByRole('button', { name: 'Khoá' })).toBeInTheDocument();

    // 45 users, 20 per page: three pages.
    expect(screen.getByRole('listitem', { name: '3' })).toBeInTheDocument();
    expect(screen.queryByRole('listitem', { name: '4' })).not.toBeInTheDocument();
    expect(requests).toHaveLength(1);
    expect(requests[0]?.query).toEqual({ page: '1', pageSize: '20' });
  });

  it('pages, sorts, searches (debounced) and filters on the server and keeps that state in the address', async () => {
    const { requests } = serveUsers([alice, bob], 45);
    const { user: appUser, location } = renderUsers();
    await screen.findByText('alice@example.com');

    await appUser.click(screen.getByRole('listitem', { name: '2' }));
    await waitFor(() => expect(requests.at(-1)?.query).toEqual({ page: '2', pageSize: '20' }));
    expect(location()).toBe('/users?page=2');

    // Sorting starts again on page 1.
    await appUser.click(screen.getByRole('columnheader', { name: /E-mail/ }));
    await waitFor(() =>
      expect(requests.at(-1)?.query).toEqual({ page: '1', pageSize: '20', sortBy: 'email', sortDir: 'asc' }),
    );
    expect(location()).toBe('/users?sortDir=asc');

    // One request for the whole word, not one per key.
    const before = requests.length;
    await appUser.type(screen.getByRole('searchbox', { name: 'Tìm theo e-mail' }), 'ali');
    await waitFor(() => expect(requests.at(-1)?.query.search).toBe('ali'));
    expect(requests.length).toBe(before + 1);
    expect(location()).toBe('/users?sortDir=asc&search=ali');

    await chooseOption(appUser, screen.getByRole('combobox', { name: 'Vai trò' }), 'Nhân viên');
    await waitFor(() => expect(requests.at(-1)?.query.role).toBe('Staff'));

    await chooseOption(appUser, screen.getByRole('combobox', { name: 'Trạng thái' }), 'Đã khoá');
    await waitFor(() =>
      expect(requests.at(-1)?.query).toEqual({
        page: '1',
        pageSize: '20',
        sortBy: 'email',
        sortDir: 'asc',
        search: 'ali',
        role: 'Staff',
        locked: 'true',
      }),
    );
    expect(location()).toBe('/users?sortDir=asc&search=ali&role=Staff&locked=true');
  });

  it('creates a user and refreshes the list', async () => {
    const { state, requests } = serveUsers([alice]);
    const created: unknown[] = [];
    const dave = user({ userId: 'u-dave', email: 'dave@example.com' });
    server.use(
      http.post(api('/users'), async ({ request }) => {
        created.push(await request.json());
        state.users = [alice, dave];
        return HttpResponse.json(dave, { status: 201 });
      }),
    );
    const { user: appUser } = renderUsers();

    const dialog = await openCreateDialog(appUser);
    await appUser.type(within(dialog).getByLabelText('E-mail'), 'dave@example.com');
    await appUser.type(within(dialog).getByLabelText('Mật khẩu'), 'Passw0rd-for-dave');
    await chooseOption(appUser, within(dialog).getByRole('combobox', { name: 'Vai trò' }), 'Nhân viên');
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    expect(await screen.findByText('Đã tạo người dùng.')).toBeInTheDocument();
    expect(created).toEqual([{ email: 'dave@example.com', password: 'Passw0rd-for-dave', role: 'Staff' }]);
    // Tag invalidation: the list is fetched again and shows the new user.
    expect(await screen.findByText('dave@example.com')).toBeInTheDocument();
    expect(requests).toHaveLength(2);
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('shows a duplicate e-mail (409) on the e-mail field and keeps the dialog open', async () => {
    const { requests } = serveUsers([alice]);
    server.use(http.post(api('/users'), () => problem(409, { errorCode: 'user_email_exists' })));
    const { user: appUser } = renderUsers();

    const dialog = await openCreateDialog(appUser);
    await appUser.type(within(dialog).getByLabelText('E-mail'), 'alice@example.com');
    await appUser.type(within(dialog).getByLabelText('Mật khẩu'), 'Passw0rd-for-alice');
    await chooseOption(appUser, within(dialog).getByRole('combobox', { name: 'Vai trò' }), 'Quản lý');
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    const error = await within(dialog).findByText('Đã có người dùng với địa chỉ e-mail này.');
    expect(error.closest('.ant-form-item')).toContainElement(within(dialog).getByLabelText('E-mail'));
    expect(within(dialog).getByLabelText('E-mail')).toHaveValue('alice@example.com');
    // Nothing changed on the server, so the list is not fetched again.
    expect(requests).toHaveLength(1);
  });

  it('shows password policy errors of the API on the password field, in the wording the API sent', async () => {
    serveUsers([alice]);
    server.use(
      http.post(api('/users'), () =>
        problem(400, {
          errorCode: 'validation_failed',
          errors: { password: ['Mật khẩu phải có ít nhất 12 ký tự.', 'Mật khẩu phải có chữ số.'] },
        }),
      ),
    );
    const { user: appUser } = renderUsers();

    const dialog = await openCreateDialog(appUser);
    await appUser.type(within(dialog).getByLabelText('E-mail'), 'erin@example.com');
    await appUser.type(within(dialog).getByLabelText('Mật khẩu'), 'weak');
    await chooseOption(appUser, within(dialog).getByRole('combobox', { name: 'Vai trò' }), 'Nhân viên');
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    const first = await within(dialog).findByText('Mật khẩu phải có ít nhất 12 ký tự.');
    expect(first.closest('.ant-form-item')).toContainElement(within(dialog).getByLabelText('Mật khẩu'));
    expect(within(dialog).getByText('Mật khẩu phải có chữ số.')).toBeInTheDocument();
  });

  it('locks a user after confirmation and refreshes the list', async () => {
    const { state, requests } = serveUsers([alice, bob]);
    const locked: string[] = [];
    server.use(
      http.post(api('/users/:id/lock'), ({ params }) => {
        locked.push(String(params.id));
        const lockedBob = { ...bob, isLocked: true, lockoutEnd: '9999-12-31T23:59:59.9999999+00:00' };
        state.users = [alice, lockedBob];
        return HttpResponse.json(lockedBob);
      }),
    );
    const { user: appUser } = renderUsers();
    await screen.findByText('bob@example.com');

    await appUser.click(within(rowOf('bob@example.com')).getByRole('button', { name: 'Khoá' }));
    const confirm = await screen.findByRole('dialog');
    expect(confirm).toHaveTextContent('bob@example.com sẽ bị đăng xuất');
    expect(locked).toEqual([]); // nothing happens before the confirmation
    await appUser.click(within(confirm).getByRole('button', { name: 'Khoá' }));

    expect(await screen.findByText('Đã khoá người dùng.')).toBeInTheDocument();
    expect(locked).toEqual(['u-bob']);
    await waitFor(() => expect(within(rowOf('bob@example.com')).getByText('Đã khoá')).toBeInTheDocument());
    expect(within(rowOf('bob@example.com')).getByRole('button', { name: 'Mở khoá' })).toBeInTheDocument();
    expect(requests).toHaveLength(2);
  });

  it('translates last_admin and cannot_modify_self from the API', async () => {
    serveUsers([alice, bob]);
    server.use(
      http.post(api('/users/:id/lock'), () => problem(409, { errorCode: 'last_admin' })),
      http.put(api('/users/:id/role'), () => problem(409, { errorCode: 'cannot_modify_self' })),
    );
    const { user: appUser } = renderUsers();
    await screen.findByText('bob@example.com');

    await appUser.click(within(rowOf('bob@example.com')).getByRole('button', { name: 'Khoá' }));
    await appUser.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Khoá' }));
    expect(
      await screen.findByText('Đây là quản trị viên đang hoạt động cuối cùng. Hệ thống phải còn ít nhất một người.'),
    ).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await appUser.click(within(rowOf('alice@example.com')).getByRole('button', { name: 'Đổi vai trò' }));
    const dialog = await screen.findByRole('dialog');
    await chooseOption(appUser, within(dialog).getByRole('combobox', { name: 'Vai trò' }), 'Nhân viên');
    await appUser.click(within(dialog).getByRole('button', { name: 'Lưu' }));
    expect(
      await within(dialog).findByText('Bạn không thể tự đổi vai trò hoặc tự khoá tài khoản của mình.'),
    ).toBeInTheDocument();
  });

  it('resets a password and shows policy errors on the new-password field', async () => {
    serveUsers([alice]);
    const bodies: unknown[] = [];
    server.use(
      http.post(api('/users/:id/reset-password'), async ({ request }) => {
        const body = (await request.json()) as { newPassword: string };
        bodies.push(body);
        return body.newPassword === 'weak'
          ? problem(400, { errorCode: 'validation_failed', errors: { newPassword: ['Mật khẩu quá ngắn.'] } })
          : HttpResponse.json(alice);
      }),
    );
    const { user: appUser } = renderUsers();
    await screen.findByText('alice@example.com');

    await appUser.click(within(rowOf('alice@example.com')).getByRole('button', { name: 'Đặt lại mật khẩu' }));
    const dialog = await screen.findByRole('dialog');
    await appUser.type(within(dialog).getByLabelText('Mật khẩu mới'), 'weak');
    await appUser.click(within(dialog).getByRole('button', { name: 'Lưu' }));
    expect(await within(dialog).findByText('Mật khẩu quá ngắn.')).toBeInTheDocument();

    await appUser.clear(within(dialog).getByLabelText('Mật khẩu mới'));
    await appUser.type(within(dialog).getByLabelText('Mật khẩu mới'), 'Passw0rd-temporary');
    await appUser.click(within(dialog).getByRole('button', { name: 'Lưu' }));

    expect(await screen.findByText('Đã đặt lại mật khẩu.')).toBeInTheDocument();
    expect(bodies).toEqual([{ newPassword: 'weak' }, { newPassword: 'Passw0rd-temporary' }]);
  });
});

describe('unexpected errors', () => {
  it('shows a notification with the correlation id of the server for a 500', async () => {
    server.use(
      http.get(api('/users'), () => problem(500, { errorCode: 'internal_error', correlationId: 'corr-500-abc' })),
    );
    renderUsers();

    expect(await screen.findByText('Đã xảy ra lỗi')).toBeInTheDocument();
    expect(screen.getByText('Máy chủ báo lỗi. Vui lòng thử lại sau.')).toBeInTheDocument();
    expect(screen.getByTestId('correlation-id')).toHaveTextContent('corr-500-abc');
  });

  it('shows the correlation id this client sent when the server cannot be reached', async () => {
    const sent: (string | null)[] = [];
    server.use(
      http.get(api('/users'), ({ request }) => {
        sent.push(request.headers.get('X-Correlation-Id'));
        return HttpResponse.error();
      }),
    );
    renderUsers();

    expect(await screen.findByText('Không kết nối được máy chủ. Hãy kiểm tra mạng và thử lại.')).toBeInTheDocument();
    expect(sent[0]).toMatch(/^[0-9a-f-]{32,36}$/);
    expect(screen.getByTestId('correlation-id')).toHaveTextContent(sent.at(-1) ?? 'missing');
  });
});

describe('language switch', () => {
  it('changes our texts, AntD texts and the Accept-Language header, and stores the choice', async () => {
    const { requests } = serveUsers([], 0);
    const { user: appUser } = renderUsers();

    expect(await screen.findByRole('heading', { name: 'Người dùng' })).toBeInTheDocument();
    expect((await screen.findAllByText('Trống')).length).toBeGreaterThan(0); // AntD's own "no data" text (vi_VN)
    expect(requests[0]?.language).toBe('vi');
    expect(document.documentElement.lang).toBe('vi');

    await appUser.click(screen.getByRole('radio', { name: 'EN' }));

    expect(await screen.findByRole('heading', { name: 'Users' })).toBeInTheDocument();
    expect(screen.getAllByText('No data').length).toBeGreaterThan(0); // AntD locale en_US
    expect(screen.queryByText('Trống')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Products' })).toBeInTheDocument();
    expect(screen.getByRole('searchbox', { name: 'Search by e-mail' })).toBeInTheDocument();
    expect(document.documentElement.lang).toBe('en');

    // The next request tells the API which language to answer in.
    await appUser.type(screen.getByRole('searchbox', { name: 'Search by e-mail' }), 'x');
    await waitFor(() => expect(requests.at(-1)?.language).toBe('en'));

    // The choice is kept for the next visit (read again at start-up).
    expect(window.localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('en');
    expect(readStoredLanguage()).toBe('en');
  });

  it('changes text only: address, table state, open dialog, typed values and the layout stay as they are', async () => {
    const { requests } = serveUsers([alice, bob], 45);
    const route = '/users?page=2&search=example&sortDir=desc';
    const { user: appUser, location } = renderUsers(route);
    await screen.findByText('alice@example.com');

    // An open create dialog with typed values.
    const dialog = await openCreateDialog(appUser);
    await appUser.type(within(dialog).getByLabelText('E-mail'), 'new.user@example.com');
    await appUser.type(within(dialog).getByLabelText('Mật khẩu'), 'Typed-passw0rd');
    await chooseOption(appUser, within(dialog).getByRole('combobox', { name: 'Vai trò' }), 'Quản lý');

    const snapshot = () => ({
      location: location(),
      listRequests: requests.length,
      // DOM nodes: a remount would create new ones.
      searchInput: screen.getByRole('searchbox'),
      emailInput: within(screen.getByRole('dialog')).getByRole('textbox'),
      table: document.querySelector('table'),
      sider: screen.getByTestId('app-sider'),
      header: screen.getByTestId('app-header'),
      // Fixed layout: widths and heights are inline constants, classes are the same.
      siderStyle: screen.getByTestId('app-sider').getAttribute('style'),
      siderClass: screen.getByTestId('app-sider').className,
      headerStyle: screen.getByTestId('app-header').getAttribute('style'),
      userBlockStyle: screen.getByTestId('app-user').getAttribute('style'),
      languageSwitchStyle: screen.getByRole('radiogroup').getAttribute('style'),
      createButtonStyle: screen.getAllByRole('button', { name: /Thêm người dùng|New user/ })[0]?.getAttribute('style'),
      columnWidths: [...document.querySelectorAll('table colgroup col')].map((col) => col.getAttribute('style')),
      menuItems: screen.getAllByRole('menuitem').length,
      activePage: document.querySelector('.ant-pagination-item-active')?.textContent,
      sort: screen.getAllByRole('columnheader')[0]?.getAttribute('aria-sort'),
      searchValue: screen.getByRole('searchbox').getAttribute('value'),
      emailValue: (within(screen.getByRole('dialog')).getByRole('textbox') as HTMLInputElement).value,
      passwordValue: (screen.getByRole('dialog').querySelector('input[type="password"]') as HTMLInputElement).value,
      rows: [...document.querySelectorAll('tbody tr.ant-table-row')].map((row) => row.getAttribute('data-row-key')),
    });

    const before = snapshot();
    expect(before.location).toBe(route);
    expect(before.listRequests).toBe(1);
    expect(before.activePage).toBe('2');
    expect(before.sort).toBe('descending');
    expect(before.searchValue).toBe('example');
    expect(before.emailValue).toBe('new.user@example.com');
    expect(before.passwordValue).toBe('Typed-passw0rd');
    expect(before.siderStyle).toContain('width: 240px');
    expect(before.headerStyle).toContain('height: 56px');
    expect(before.columnWidths.length).toBeGreaterThan(0);

    await appUser.click(screen.getByRole('radio', { name: 'EN' }));

    // Texts changed, in the page, the dialog and AntD's own parts ...
    expect(await screen.findByRole('heading', { name: 'Users' })).toBeInTheDocument();
    expect(within(screen.getByRole('dialog')).getByText('New user')).toBeInTheDocument();
    expect(within(screen.getByRole('dialog')).getByText('Manager')).toBeInTheDocument(); // the chosen role, relabelled
    expect(screen.getByText('20 / page')).toBeInTheDocument();

    // ... and nothing else: same nodes (no remount), same state, same layout, no new request.
    expect(snapshot()).toEqual(before);

    await appUser.click(screen.getByRole('radio', { name: 'VI' }));

    expect(await screen.findByRole('heading', { name: 'Người dùng' })).toBeInTheDocument();
    expect(screen.getByText('20 / trang')).toBeInTheDocument();
    expect(snapshot()).toEqual(before);
  });
});
