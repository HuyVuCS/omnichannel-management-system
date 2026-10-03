import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import type { PagedResult } from '@/shared/api/types';
import { columnTexts, expectLanguageSwitchChangesTextOnly, findDialog, rowOf } from '@/test/interactions';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';
import type { Customer } from './customersApi';

function customer(overrides: Partial<Customer> & Pick<Customer, 'customerId' | 'name'>): Customer {
  return {
    phone: null,
    email: null,
    address: null,
    createdAt: '2026-09-30T03:15:00+00:00',
    updatedAt: null,
    ...overrides,
  };
}

const an = customer({
  customerId: 'c-an',
  name: 'Nguyễn Văn An',
  phone: '+84 90 123 4567',
  email: 'an@example.com',
  address: '12 Lê Lợi, Phường Bến Nghé, Quận 1, Thành phố Hồ Chí Minh',
});
const binh = customer({ customerId: 'c-binh', name: 'Trần Thị Bình' });

function serveCustomers(initial: Customer[], totalCount = initial.length) {
  const state = { customers: initial, totalCount };
  const requests: Record<string, string>[] = [];
  server.use(
    http.get(api('/customers'), ({ request }) => {
      const url = new URL(request.url);
      requests.push(Object.fromEntries(url.searchParams));
      const page: PagedResult<Customer> = {
        items: state.customers,
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 20),
        totalCount: state.totalCount,
      };
      return HttpResponse.json(page);
    }),
  );
  return { state, requests };
}

function renderCustomers(user: AuthUser = staffUser, route = '/customers') {
  return renderApp({ route, session: sessionOf(user) });
}

describe('customers page', () => {
  it('renders the page of customers; the long address is one truncated line with the full text available', async () => {
    const { requests } = serveCustomers([an, binh]);
    renderCustomers();

    expect(await screen.findByText('Nguyễn Văn An')).toBeInTheDocument();
    expect(columnTexts('Tên khách hàng')).toEqual(['Nguyễn Văn An', 'Trần Thị Bình']);
    expect(columnTexts('Điện thoại')).toEqual(['+84 90 123 4567', '']);
    expect(columnTexts('E-mail')).toEqual(['an@example.com', '']);

    const address = screen.getByText(an.address ?? '');
    expect(address).toHaveClass('ant-typography-ellipsis');
    expect(requests).toEqual([{ page: '1', pageSize: '20' }]);
  });

  it('restores paging, sorting and search from the address (reload) and writes changes back to it', async () => {
    const { requests } = serveCustomers([an, binh], 45);
    const { user: appUser, location } = renderCustomers(
      staffUser,
      '/customers?page=2&sortBy=createdAt&sortDir=desc&search=an',
    );

    await screen.findByText('Nguyễn Văn An');
    expect(requests).toEqual([{ page: '2', pageSize: '20', sortBy: 'createdAt', sortDir: 'desc', search: 'an' }]);
    expect(document.querySelector('.ant-pagination-item-active')).toHaveTextContent('2');
    expect(screen.getByRole('columnheader', { name: /Ngày tạo/ })).toHaveAttribute('aria-sort', 'descending');
    expect(screen.getByRole('searchbox')).toHaveValue('an');

    await appUser.click(screen.getByRole('columnheader', { name: /Tên khách hàng/ }));
    await waitFor(() =>
      expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', sortBy: 'name', sortDir: 'asc', search: 'an' }),
    );
    expect(location()).toBe('/customers?sortDir=asc&search=an');
  });

  it('creates a customer; empty optional fields are sent as null; then refreshes the list', async () => {
    const { state, requests } = serveCustomers([an]);
    const bodies: unknown[] = [];
    server.use(
      http.post(api('/customers'), async ({ request }) => {
        bodies.push(await request.json());
        state.customers = [an, binh];
        return HttpResponse.json(binh, { status: 201 });
      }),
    );
    const { user: appUser } = renderCustomers();

    await appUser.click(await screen.findByRole('button', { name: /Thêm khách hàng/ }));
    const dialog = await findDialog('Thêm khách hàng');
    await appUser.type(within(dialog).getByLabelText('Tên khách hàng'), 'Trần Thị Bình');
    await appUser.type(within(dialog).getByLabelText('Điện thoại'), ' 0901234567 ');
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    expect(await screen.findByText('Đã thêm khách hàng.')).toBeInTheDocument();
    expect(bodies).toEqual([{ name: 'Trần Thị Bình', phone: '0901234567', email: null, address: null }]);
    expect(await screen.findByText('Trần Thị Bình')).toBeInTheDocument();
    expect(requests).toHaveLength(2);
  });

  it('puts the API rules (phone characters, e-mail) on their fields and does not refresh the list', async () => {
    const { requests } = serveCustomers([an]);
    server.use(
      http.post(api('/customers'), () =>
        problem(400, {
          errorCode: 'validation_failed',
          errors: {
            phone: ["'phone' chỉ được chứa chữ số, dấu cách và các ký tự + - ( )."],
            email: ["'email' không phải là email hợp lệ."],
          },
        }),
      ),
    );
    const { user: appUser } = renderCustomers();

    await appUser.click(await screen.findByRole('button', { name: /Thêm khách hàng/ }));
    const dialog = await findDialog('Thêm khách hàng');
    await appUser.type(within(dialog).getByLabelText('Tên khách hàng'), 'Khách lỗi');
    await appUser.type(within(dialog).getByLabelText('Điện thoại'), 'abc');
    await appUser.type(within(dialog).getByLabelText('E-mail'), 'x@example.com');
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    const phoneError = await within(dialog).findByText("'phone' chỉ được chứa chữ số, dấu cách và các ký tự + - ( ).");
    expect(phoneError.closest('.ant-form-item')).toContainElement(within(dialog).getByLabelText('Điện thoại'));
    const emailError = within(dialog).getByText("'email' không phải là email hợp lệ.");
    expect(emailError.closest('.ant-form-item')).toContainElement(within(dialog).getByLabelText('E-mail'));
    expect(requests).toHaveLength(1);
  });

  it('drops the API errors of the optional fields when the form is submitted again', async () => {
    serveCustomers([an]);
    const phoneMessage = "'phone' chỉ được chứa chữ số, dấu cách và các ký tự + - ( ).";
    const addressMessage = "'address' dài tối đa 500 ký tự (máy chủ).";
    let attempt = 0;
    server.use(
      http.post(api('/customers'), () => {
        attempt += 1;
        return attempt === 1
          ? problem(400, {
              errorCode: 'validation_failed',
              errors: { phone: [phoneMessage], address: [addressMessage] },
            })
          : // The second attempt fails for another reason: the dialog stays open and shows what it holds.
            problem(409, { errorCode: 'concurrency_conflict' });
      }),
    );
    const { user: appUser } = renderCustomers();

    await appUser.click(await screen.findByRole('button', { name: /Thêm khách hàng/ }));
    const dialog = await findDialog('Thêm khách hàng');
    await appUser.type(within(dialog).getByLabelText('Tên khách hàng'), 'Khách lỗi');
    await appUser.type(within(dialog).getByLabelText('Điện thoại'), 'abc');
    await appUser.type(within(dialog).getByLabelText('Địa chỉ'), 'Một địa chỉ');
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));
    expect(await within(dialog).findByText(phoneMessage)).toBeInTheDocument();
    expect(within(dialog).getByText(addressMessage)).toBeInTheDocument();

    // The user removes what the API complained about and sends the form again.
    await appUser.clear(within(dialog).getByLabelText('Điện thoại'));
    await appUser.clear(within(dialog).getByLabelText('Địa chỉ'));
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    expect(
      await within(dialog).findByText('Dữ liệu đã bị người khác thay đổi. Hãy tải lại rồi thử lại.'),
    ).toBeInTheDocument();
    // Phone and address have no rule in the form (theirs are the API's): nothing re-validates them on submit. (The
    // form removes an error text a moment after its field lost it.)
    await waitFor(() => {
      expect(within(dialog).queryByText(phoneMessage)).not.toBeInTheDocument();
      expect(within(dialog).queryByText(addressMessage)).not.toBeInTheDocument();
    });
    expect(attempt).toBe(2);
  });

  it('edits a customer with the form prefilled; then refreshes the list', async () => {
    const { state, requests } = serveCustomers([an, binh]);
    const updates: { id: string; body: unknown }[] = [];
    server.use(
      http.put(api('/customers/:id'), async ({ request, params }) => {
        updates.push({ id: String(params.id), body: await request.json() });
        const updated = { ...an, phone: '0283 822 1234' };
        state.customers = [updated, binh];
        return HttpResponse.json(updated);
      }),
    );
    const { user: appUser } = renderCustomers();
    await screen.findByText('Nguyễn Văn An');

    await appUser.click(within(rowOf('Nguyễn Văn An')).getByRole('button', { name: 'Sửa' }));
    const dialog = await findDialog('Sửa khách hàng');
    expect(within(dialog).getByLabelText('Tên khách hàng')).toHaveValue('Nguyễn Văn An');
    expect(within(dialog).getByLabelText('E-mail')).toHaveValue('an@example.com');
    expect(within(dialog).getByLabelText('Địa chỉ')).toHaveValue(an.address);

    const phone = within(dialog).getByLabelText('Điện thoại');
    await appUser.clear(phone);
    await appUser.type(phone, '0283 822 1234');
    await appUser.click(within(dialog).getByRole('button', { name: 'Lưu' }));

    expect(await screen.findByText('Đã cập nhật khách hàng.')).toBeInTheDocument();
    expect(updates).toEqual([
      {
        id: 'c-an',
        body: { name: 'Nguyễn Văn An', phone: '0283 822 1234', email: 'an@example.com', address: an.address },
      },
    ]);
    expect(await screen.findByText('0283 822 1234')).toBeInTheDocument();
    expect(requests).toHaveLength(2);
  });
});

describe('customers page: roles', () => {
  it.each([
    ['Staff', staffUser],
    ['Manager', managerUser],
    ['Admin', adminUser],
  ] as const)('%s can create and edit customers', async (_role, user) => {
    serveCustomers([an, binh]);
    renderCustomers(user);

    await screen.findByText('Nguyễn Văn An');
    expect(screen.getAllByRole('button', { name: /Thêm khách hàng/ })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Sửa' })).toHaveLength(2);
  });
});

describe('customers page: language switch', () => {
  it('changes text only: address, table state, the open form and its values stay', async () => {
    const { requests } = serveCustomers([an, binh], 45);
    const { user: appUser, location } = renderCustomers(staffUser, '/customers?page=2&sortDir=desc&search=n');
    await screen.findByText('Nguyễn Văn An');

    await appUser.click(within(rowOf('Nguyễn Văn An')).getByRole('button', { name: 'Sửa' }));
    const dialog = await findDialog('Sửa khách hàng');
    await appUser.type(within(dialog).getByLabelText('Địa chỉ'), ' (đang sửa)');

    await expectLanguageSwitchChangesTextOnly(appUser, location, {
      vietnamese: ['Tên khách hàng', 'Điện thoại', 'Sửa khách hàng', '20 / trang'],
      english: ['Name', 'Phone', 'Edit customer', '20 / page'],
      requestCount: () => requests.length,
    });
    expect(within(await findDialog('Sửa khách hàng')).getByLabelText('Địa chỉ')).toHaveValue(
      `${an.address} (đang sửa)`,
    );
  });
});

describe('customers page: dialog reopened before its close animation has finished', () => {
  it('starts from the record it was opened for, and the form-level error of the last attempt is gone', async () => {
    serveCustomers([an, binh]);
    server.use(http.put(api('/customers/:id'), () => problem(409, { errorCode: 'concurrency_conflict' })));
    // Animations on: in jsdom they never finish, so the closed dialog and its form stay mounted.
    const { user: appUser } = renderApp({ route: '/customers', session: sessionOf(staffUser), animations: true });
    await screen.findByText('Nguyễn Văn An');

    await appUser.click(within(rowOf('Nguyễn Văn An')).getByRole('button', { name: 'Sửa' }));
    let dialog = await findDialog('Sửa khách hàng');
    await waitFor(() => expect(within(dialog).getByLabelText('Tên khách hàng')).toHaveValue('Nguyễn Văn An'));
    await appUser.click(within(dialog).getByRole('button', { name: 'Lưu' }));
    expect(
      await within(dialog).findByText('Dữ liệu đã bị người khác thay đổi. Hãy tải lại rồi thử lại.'),
    ).toBeInTheDocument();
    await appUser.click(within(dialog).getByRole('button', { name: 'Huỷ' }));

    await appUser.click(within(rowOf('Trần Thị Bình')).getByRole('button', { name: 'Sửa' }));
    dialog = await findDialog('Sửa khách hàng');
    await waitFor(() => expect(within(dialog).getByLabelText('Tên khách hàng')).toHaveValue('Trần Thị Bình'));
    expect(within(dialog).getByLabelText('Điện thoại')).toHaveValue('');
    expect(within(dialog).getByLabelText('E-mail')).toHaveValue('');
    expect(within(dialog).getByLabelText('Địa chỉ')).toHaveValue('');
    expect(
      within(dialog).queryByText('Dữ liệu đã bị người khác thay đổi. Hãy tải lại rồi thử lại.'),
    ).not.toBeInTheDocument();
  });
});

/**
 * Animations off: the closed dialog and its form are destroyed, as in the browser once the close animation has ended.
 * The form instance outlives them. Without React's StrictMode (a production build; these tests) nothing else replaces
 * what it holds: the next dialog would start with the values of the previous one.
 */
describe('customers page: dialog opened again after it was closed completely', () => {
  it('shows the values of the record it was opened for, never those of the previous one', async () => {
    serveCustomers([an, binh]);
    const { user: appUser } = renderApp({ route: '/customers', session: sessionOf(staffUser) });
    await screen.findByText('Nguyễn Văn An');
    const closed = () => waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    const valuesOf = (dialog: HTMLElement) =>
      ['Tên khách hàng', 'Điện thoại', 'E-mail', 'Địa chỉ'].map(
        (label) => within(dialog).getByLabelText<HTMLInputElement>(label).value,
      );

    await appUser.click(within(rowOf('Nguyễn Văn An')).getByRole('button', { name: 'Sửa' }));
    let dialog = await findDialog('Sửa khách hàng');
    const first = valuesOf(dialog);
    expect(first[0]).toBe('Nguyễn Văn An');
    await appUser.click(within(dialog).getByRole('button', { name: 'Huỷ' }));
    await closed();

    await appUser.click(within(rowOf('Trần Thị Bình')).getByRole('button', { name: 'Sửa' }));
    dialog = await findDialog('Sửa khách hàng');
    const second = valuesOf(dialog);
    expect(second[0]).toBe('Trần Thị Bình');
    // Nothing of the first customer is left - also not in a field the second one has no value for.
    for (const value of first.filter((text) => text !== '')) {
      expect(second).not.toContain(value);
    }

    await appUser.click(within(dialog).getByRole('button', { name: 'Huỷ' }));
    await closed();

    await appUser.click(screen.getByRole('button', { name: /Thêm khách hàng/ }));
    dialog = await findDialog('Thêm khách hàng');
    expect(valuesOf(dialog)).toEqual(['', '', '', '']);
  });
});
