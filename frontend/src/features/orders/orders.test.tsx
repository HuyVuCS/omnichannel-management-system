import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import type { PagedResult } from '@/shared/api/types';
import {
  AN_ID,
  anCustomer,
  BINH_ID,
  binhCustomer,
  chooseCustomer,
  customer,
  openOptions,
  serveCustomerCatalog,
} from '@/test/catalog';
import { chooseOption, columnTexts, expectLanguageSwitchChangesTextOnly, rowOf, settled } from '@/test/interactions';
import { ORDER_ID, orderDetail, orderSummary, serveChannels } from '@/test/orders';
import { renderApp } from '@/test/renderApp';
import { serveExportRequests, serveExports } from '@/test/reports';
import { api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';
import type { OrderSummary } from './ordersApi';

const SECOND_ORDER_ID = '0199a1b2-0000-7000-8000-000000000002';

const reserved = orderSummary();
const insufficient = orderSummary({
  orderId: SECOND_ORDER_ID,
  externalOrderId: 'POS-20260930-ZZ9911',
  status: 'InsufficientStock',
  totalAmount: 45000,
  itemCount: 1,
  channel: { channelId: 'channel-pos', slug: 'pos', name: 'POS' },
  customer: { customerId: BINH_ID, name: 'Trần Thị Bình' },
  createdAt: new Date(2026, 8, 30, 10, 15).toISOString(),
});

/** `GET /api/orders`; records the query string of every request. `answer` can replace the response. */
function serveOrders(initial: OrderSummary[], totalCount = initial.length) {
  const state: { items: OrderSummary[]; totalCount: number; answer?: () => Response } = {
    items: initial,
    totalCount,
  };
  const requests: Record<string, string>[] = [];
  server.use(
    http.get(api('/orders'), ({ request }) => {
      const url = new URL(request.url);
      requests.push(Object.fromEntries(url.searchParams));
      if (state.answer) {
        return state.answer();
      }

      const page: PagedResult<OrderSummary> = {
        items: state.items,
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 20),
        totalCount: state.totalCount,
      };
      return HttpResponse.json(page);
    }),
  );
  return { state, requests };
}

/** The order list with its channel filter options and the customer picker's endpoints. */
function serveOrderList(initial: OrderSummary[], totalCount = initial.length) {
  const channels = serveChannels();
  const customers = serveCustomerCatalog([anCustomer, binhCustomer]);
  return { ...serveOrders(initial, totalCount), channels, customers };
}

function renderOrders(route = '/orders', user: AuthUser = staffUser) {
  return renderApp({ route, session: sessionOf(user) });
}

const startInput = () => screen.getByPlaceholderText('Từ ngày');
const endInput = () => screen.getByPlaceholderText('Đến ngày');
const sortOf = (name: RegExp) => screen.getByRole('columnheader', { name }).getAttribute('aria-sort');
const filter = (name: string) => screen.getByRole('combobox', { name });

describe('orders list', () => {
  it('lists orders as the API sends them, newest first, and says so in the table', async () => {
    const { requests } = serveOrderList([reserved, insufficient]);
    renderOrders();

    expect(await screen.findByText('WEB-20261001-ABC123')).toBeInTheDocument();
    expect(columnTexts('Ngày tạo')).toEqual(['01/10/2026 08:05', '30/09/2026 10:15']);
    expect(columnTexts('Mã đơn hàng bên ngoài')).toEqual(['WEB-20261001-ABC123', 'POS-20260930-ZZ9911']);
    expect(columnTexts('Kênh bán')).toEqual(['Website', 'POS']);
    expect(columnTexts('Khách hàng')).toEqual(['Nguyễn Văn An', 'Trần Thị Bình']);
    expect(columnTexts('Trạng thái')).toEqual(['Đã giữ hàng', 'Không đủ hàng']);
    expect(columnTexts('Số dòng')).toEqual(['2', '1']);
    expect(columnTexts('Tổng tiền')).toEqual(['1.234.567 ₫', '45.000 ₫']);

    // One status colour per status.
    expect(within(rowOf('WEB-20261001-ABC123')).getByText('Đã giữ hàng').closest('.ant-tag')).toHaveClass(
      'ant-tag-processing',
    );
    expect(within(rowOf('POS-20260930-ZZ9911')).getByText('Không đủ hàng').closest('.ant-tag')).toHaveClass(
      'ant-tag-warning',
    );

    // Nothing in the address: the API's default order (newest first) is not asked for, and the table shows it.
    expect(requests).toEqual([{ page: '1', pageSize: '20' }]);
    expect(sortOf(/Ngày tạo/)).toBe('descending');
    expect(sortOf(/Tổng tiền/)).toBeNull();
    expect(sortOf(/Mã đơn hàng bên ngoài/)).toBeNull();
  });

  it('puts the columns users scan first: the status ends inside the content area of a 1280 px window', async () => {
    serveOrderList([reserved, insufficient]);
    renderOrders();
    await screen.findByText('WEB-20261001-ABC123');

    const headers = [...document.querySelectorAll('thead th')].map((th) => th.textContent);
    expect(headers).toEqual([
      'Ngày tạo',
      'Mã đơn hàng bên ngoài',
      'Trạng thái',
      'Tổng tiền',
      'Khách hàng',
      'Kênh bán',
      'Số dòng',
      'Thao tác',
    ]);

    // jsdom has no layout: the fixed column widths are what can be checked. 1280 px window - 240 px menu - 2 × 24 px
    // padding = 992 px.
    const widths = [...document.querySelectorAll<HTMLElement>('table colgroup col')].map((col) =>
      Number.parseInt(col.style.width, 10),
    );
    const upToStatus = widths.slice(0, headers.indexOf('Trạng thái') + 1).reduce((sum, width) => sum + width, 0);
    const upToTotal = widths.slice(0, headers.indexOf('Tổng tiền') + 1).reduce((sum, width) => sum + width, 0);
    expect(upToStatus).toBeLessThanOrEqual(992);
    expect(upToTotal).toBeLessThanOrEqual(992);
  });

  it('restores search, sort, every filter and the page from the address (reload) and sends the local day range with its offset', async () => {
    const { requests, customers } = serveOrderList([insufficient], 45);
    const address =
      `/orders?search=POS&sortBy=totalAmount&sortDir=desc&channel=pos&status=InsufficientStock` +
      `&customerId=${BINH_ID}&from=2026-09-01&to=2026-09-30&page=2`;
    const { location } = renderOrders(address);

    expect(await screen.findByText('POS-20260930-ZZ9911')).toBeInTheDocument();
    expect(requests).toHaveLength(1);
    const { from, to, ...rest } = requests[0]!;
    expect(rest).toEqual({
      page: '2',
      pageSize: '20',
      search: 'POS',
      sortBy: 'totalAmount',
      sortDir: 'desc',
      channel: 'pos',
      status: 'InsufficientStock',
      customerId: BINH_ID,
    });
    // The local start of the first day and the local end of the last day, written with the UTC offset.
    expect(from).toMatch(/^2026-09-01T00:00:00[+-]\d{2}:\d{2}$/);
    expect(new Date(from!).getTime()).toBe(new Date(2026, 8, 1, 0, 0, 0, 0).getTime());
    expect(to).toMatch(/^2026-09-30T23:59:59\.999[+-]\d{2}:\d{2}$/);
    expect(new Date(to!).getTime()).toBe(new Date(2026, 8, 30, 23, 59, 59, 999).getTime());

    // The controls show the state of the address.
    expect(screen.getByRole('searchbox')).toHaveValue('POS');
    await waitFor(() => expect(filter('Kênh bán').closest('.ant-select')).toHaveTextContent('POS'));
    expect(filter('Trạng thái').closest('.ant-select')).toHaveTextContent('Không đủ hàng');
    // The customer's name comes from GET /api/customers/{id}; the customer list is not loaded for it.
    await waitFor(() => expect(filter('Khách hàng').closest('.ant-select')).toHaveTextContent('Trần Thị Bình'));
    expect(customers.detailRequests).toEqual([BINH_ID]);
    expect(customers.listRequests).toEqual([]);
    expect(startInput()).toHaveValue('01/09/2026');
    expect(endInput()).toHaveValue('30/09/2026');
    expect(document.querySelector('.ant-pagination-item-active')).toHaveTextContent('2');
    // The sort indicator is the order the API was asked for.
    expect(sortOf(/Tổng tiền/)).toBe('descending');
    expect(sortOf(/Ngày tạo/)).toBeNull();
    expect(location()).toBe(address);
  });

  it('writes search and each filter to the address, starts on page 1 again, and "Clear filters" removes the filters', async () => {
    const { requests } = serveOrderList([reserved, insufficient], 45);
    const { user: appUser, location } = renderOrders('/orders?page=3');
    await screen.findByText('WEB-20261001-ABC123');
    expect(requests).toEqual([{ page: '3', pageSize: '20' }]);

    await chooseOption(appUser, filter('Kênh bán'), 'POS');
    await waitFor(() => expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', channel: 'pos' }));
    expect(location()).toBe('/orders?channel=pos');

    await appUser.click(screen.getByRole('listitem', { name: '2' }));
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ page: '2', channel: 'pos' }));

    await chooseOption(appUser, filter('Trạng thái'), 'Không đủ hàng');
    await waitFor(() =>
      expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', channel: 'pos', status: 'InsufficientStock' }),
    );

    await appUser.click(screen.getByRole('listitem', { name: '2' }));
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ page: '2' }));
    await chooseCustomer(appUser, filter('Khách hàng'), 'Nguyễn Văn An');
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ page: '1', customerId: AN_ID }));
    expect(location()).toBe(`/orders?channel=pos&status=InsufficientStock&customerId=${AN_ID}`);

    // One request per pause in typing, and the search starts on the first page.
    await appUser.click(screen.getByRole('listitem', { name: '2' }));
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ page: '2' }));
    const before = requests.length;
    await appUser.type(screen.getByRole('searchbox'), 'ZZ99');
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ search: 'ZZ99', page: '1' }));
    expect(requests.length).toBe(before + 1);

    // A range of local days.
    await appUser.click(startInput());
    await appUser.type(startInput(), '05/09/2026{Enter}');
    await appUser.type(endInput(), '06/09/2026{Enter}');
    await waitFor(() => expect(requests.at(-1)?.to).toBeDefined());
    expect(new Date(requests.at(-1)!.from!).getTime()).toBe(new Date(2026, 8, 5).getTime());
    expect(new Date(requests.at(-1)!.to!).getTime()).toBe(new Date(2026, 8, 6, 23, 59, 59, 999).getTime());
    expect(location()).toBe(
      `/orders?channel=pos&status=InsufficientStock&customerId=${AN_ID}&search=ZZ99&from=2026-09-05&to=2026-09-06`,
    );

    // The filters go; the search box has its own clear button and stays.
    await appUser.click(screen.getByRole('button', { name: 'Xoá bộ lọc' }));
    await waitFor(() => expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', search: 'ZZ99' }));
    expect(location()).toBe('/orders?search=ZZ99');
    expect(startInput()).toHaveValue('');
    expect(endInput()).toHaveValue('');
    expect(filter('Khách hàng').closest('.ant-select')).not.toHaveTextContent('Nguyễn Văn An');
    expect(filter('Kênh bán').closest('.ant-select')).not.toHaveTextContent('POS');

    await settled();
  });

  it('sorts through the address: created ascending, then the other fields, and back to the default order', async () => {
    const { requests } = serveOrderList([reserved, insufficient], 45);
    const { user: appUser, location } = renderOrders('/orders?page=2');
    await screen.findByText('WEB-20261001-ABC123');

    // The default is newest first, so the first click on "created" asks for the oldest first.
    await appUser.click(screen.getByRole('columnheader', { name: /Ngày tạo/ }));
    await waitFor(() =>
      expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', sortBy: 'createdAt', sortDir: 'asc' }),
    );
    expect(location()).toBe('/orders?sortDir=asc');
    expect(sortOf(/Ngày tạo/)).toBe('ascending');

    // One more click returns to the default order.
    await appUser.click(screen.getByRole('columnheader', { name: /Ngày tạo/ }));
    await waitFor(() => expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20' }));
    expect(location()).toBe('/orders');
    expect(sortOf(/Ngày tạo/)).toBe('descending');

    await appUser.click(screen.getByRole('columnheader', { name: /Tổng tiền/ }));
    await waitFor(() =>
      expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', sortBy: 'totalAmount', sortDir: 'asc' }),
    );
    expect(location()).toBe('/orders?sortBy=totalAmount&sortDir=asc');
    expect(sortOf(/Tổng tiền/)).toBe('ascending');
    expect(sortOf(/Ngày tạo/)).toBeNull();

    await appUser.click(screen.getByRole('columnheader', { name: /Mã đơn hàng bên ngoài/ }));
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ sortBy: 'externalOrderId', sortDir: 'asc' }));
    expect(sortOf(/Tổng tiền/)).toBeNull();

    await settled();
  });

  it('ignores address values that are not valid and never sends them; the channel slug is sent as it is', async () => {
    const { requests } = serveOrderList([reserved]);
    renderOrders(
      '/orders?status=Nope&customerId=abc&from=2026-13-45&to=yesterday&channel=no-such-channel&sortBy=secret&page=x',
    );

    await screen.findByText('WEB-20261001-ABC123');
    expect(requests).toEqual([{ page: '1', pageSize: '20', channel: 'no-such-channel' }]);
    expect(startInput()).toHaveValue('');
    expect(endInput()).toHaveValue('');
    // An unknown sort field is not a sort: the default order applies and is shown.
    expect(sortOf(/Ngày tạo/)).toBe('descending');
  });

  it('shows what the API rejects (an inverted range typed into the address) above the table, not an empty list', async () => {
    const { state, requests } = serveOrderList([reserved]);
    state.answer = () =>
      problem(400, {
        errorCode: 'validation_failed',
        errors: { to: ["'Đến ngày' không được sớm hơn 'Từ ngày'."] },
      });
    const { user: appUser } = renderOrders('/orders?from=2026-09-02&to=2026-09-01');

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent("'Đến ngày' không được sớm hơn 'Từ ngày'.");
    expect(requests).toHaveLength(1);
    expect(document.querySelectorAll('tbody tr.ant-table-row')).toHaveLength(0);
    // Not reported as an unexpected error.
    expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();

    // After a fix the list is back.
    state.answer = undefined;
    await appUser.click(screen.getByRole('button', { name: /Làm mới/ }));
    expect(await screen.findByText('WEB-20261001-ABC123')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('opens the detail with the list address, and "Back" on the detail returns to the same page, sort and filters', async () => {
    const { requests } = serveOrderList([reserved, insufficient], 45);
    server.use(http.get(api('/orders/:id'), () => HttpResponse.json(orderDetail())));
    const address = '/orders?sortBy=totalAmount&sortDir=desc&status=Reserved&page=2';
    const { user: appUser, location, router } = renderOrders(address);
    await screen.findByText('WEB-20261001-ABC123');

    // The external order id is the link ...
    await appUser.click(screen.getByRole('link', { name: 'WEB-20261001-ABC123' }));
    expect(await screen.findByRole('heading', { name: 'Đơn hàng WEB-20261001-ABC123' })).toBeInTheDocument();
    expect(location()).toBe(`/orders/${ORDER_ID}`);
    expect(router.state.location.state).toEqual({ from: address });

    await appUser.click(screen.getByRole('button', { name: /Quay lại/ }));
    await screen.findByRole('heading', { name: 'Đơn hàng' });
    expect(location()).toBe(address);
    await waitFor(() =>
      expect(requests.at(-1)).toEqual({
        page: '2',
        pageSize: '20',
        sortBy: 'totalAmount',
        sortDir: 'desc',
        status: 'Reserved',
      }),
    );

    // ... and so is the "Detail" action of the row.
    await screen.findByText('POS-20260930-ZZ9911');
    await appUser.click(within(rowOf('POS-20260930-ZZ9911')).getByRole('button', { name: 'Chi tiết' }));
    await waitFor(() => expect(location()).toBe(`/orders/${insufficient.orderId}`));
    expect(router.state.location.state).toEqual({ from: address });

    await settled();
  });

  it('asks the API again on "Refresh" and whenever the page is opened', async () => {
    const { state, requests } = serveOrderList([reserved]);
    const { user: appUser } = renderOrders('/orders', managerUser);
    await screen.findByText('WEB-20261001-ABC123');

    state.items = [insufficient, reserved];
    await appUser.click(screen.getByRole('button', { name: /Làm mới/ }));
    expect(await screen.findByText('POS-20260930-ZZ9911')).toBeInTheDocument();
    expect(requests).toHaveLength(2);

    // Orders are created in the background: coming back to the page asks again instead of showing the cached list.
    await appUser.click(screen.getByRole('link', { name: 'Khách hàng' }));
    await screen.findByRole('heading', { name: 'Khách hàng' });
    await appUser.click(screen.getByRole('link', { name: 'Đơn hàng' }));
    await screen.findByRole('heading', { name: 'Đơn hàng' });
    await waitFor(() => expect(requests).toHaveLength(3));

    await settled();
  });
});

describe('orders list: language switch', () => {
  it('changes text only: address, sort, filters, their controls and the page stay', async () => {
    const { requests, channels, customers } = serveOrderList([reserved, insufficient], 45);
    const address =
      `/orders?search=20&sortBy=totalAmount&sortDir=desc&channel=pos&status=InsufficientStock` +
      `&customerId=${BINH_ID}&from=2026-09-01&to=2026-09-30&page=2`;
    const { user: appUser, location } = renderOrders(address);
    await screen.findByText('WEB-20261001-ABC123');
    await waitFor(() => expect(filter('Khách hàng').closest('.ant-select')).toHaveTextContent('Trần Thị Bình'));
    await waitFor(() => expect(filter('Kênh bán').closest('.ant-select')).toHaveTextContent('POS'));

    await expectLanguageSwitchChangesTextOnly(appUser, location, {
      vietnamese: ['Đơn hàng', 'Mã đơn hàng bên ngoài', 'Tổng tiền', 'Đã giữ hàng', 'Xoá bộ lọc', '20 / trang'],
      english: ['Orders', 'External order ID', 'Total amount', 'Reserved', 'Clear filters', '20 / page'],
      requestCount: () =>
        requests.length + channels.count + customers.listRequests.length + customers.detailRequests.length,
    });

    expect(columnTexts('Ngày tạo')).toEqual(['01/10/2026 08:05', '30/09/2026 10:15']);
    expect(columnTexts('Tổng tiền')).toEqual(['1.234.567 ₫', '45.000 ₫']);
    expect(startInput()).toHaveValue('01/09/2026');
    expect(filter('Khách hàng').closest('.ant-select')).toHaveTextContent('Trần Thị Bình');
    expect(location()).toBe(address);
  });
});

describe('customer picker', () => {
  it('loads nothing until it is opened, then searches on the server after a pause in typing', async () => {
    const { customers } = serveOrderList([reserved]);
    const { user: appUser } = renderOrders();
    await screen.findByText('WEB-20261001-ABC123');
    expect(customers.listRequests).toEqual([]);
    expect(customers.detailRequests).toEqual([]);

    await appUser.click(filter('Khách hàng'));
    await waitFor(() => expect(openOptions()).toHaveLength(2));
    expect(customers.listRequests).toEqual([{ page: '1', pageSize: '20' }]);
    // The name, with the phone number or the e-mail address as secondary text.
    expect(openOptions().map((option) => option.textContent)).toEqual([
      'Nguyễn Văn An+84 90 123 4567',
      'Trần Thị Bìnhbinh@example.com',
    ]);

    // One request for the whole word, not one per key; the options are what the server found.
    await appUser.type(filter('Khách hàng'), 'bình');
    await waitFor(() => expect(openOptions()).toHaveLength(1));
    expect(customers.listRequests).toEqual([
      { page: '1', pageSize: '20' },
      { page: '1', pageSize: '20', search: 'bình' },
    ]);
    expect(openOptions()[0]).toHaveTextContent('Trần Thị Bình');
  });

  it('makes the full text of a long name and contact available on the option and on the selected value', async () => {
    const longName = 'Công ty Trách nhiệm hữu hạn Thương mại Dịch vụ Xuất nhập khẩu Hoa Sen Vàng Miền Nam';
    const longEmail = 'phong.kinh.doanh.khu.vuc.mien.nam@hoa-sen-vang-mien-nam.example.com';
    const long = customer({ customerId: AN_ID, name: longName, email: longEmail });
    serveOrders([reserved]);
    serveChannels();
    serveCustomerCatalog([long]);
    const { user: appUser } = renderOrders();
    await screen.findByText('WEB-20261001-ABC123');

    // jsdom cannot measure whether a text is cut; what the user can reach on hover is the title.
    await appUser.click(filter('Khách hàng'));
    await waitFor(() => expect(openOptions()).toHaveLength(1));
    expect(openOptions()[0]).toHaveAttribute('title', `${longName} — ${longEmail}`);

    await appUser.click(openOptions()[0]!);
    const selected = filter('Khách hàng').closest('.ant-select')!.querySelector('.ant-select-content');
    await waitFor(() => expect(selected).toHaveTextContent(longName));
    expect(selected).toHaveAttribute('title', `${longName} — ${longEmail}`);
  });

  it('shows the full name as the title of a value that came from the address', async () => {
    const longName = 'Công ty Trách nhiệm hữu hạn Thương mại Dịch vụ Xuất nhập khẩu Hoa Sen Vàng Miền Nam';
    serveOrders([reserved]);
    serveChannels();
    serveCustomerCatalog([customer({ customerId: AN_ID, name: longName })]);
    renderOrders(`/orders?customerId=${AN_ID}`);

    const selected = () => filter('Khách hàng').closest('.ant-select')!.querySelector('.ant-select-content');
    await waitFor(() => expect(selected()).toHaveTextContent(longName));
    expect(selected()).toHaveAttribute('title', longName);
  });
});

describe('orders list: export to Excel', () => {
  const exportButton = () => screen.queryByRole('button', { name: /Xuất Excel/ });

  it('takes a Manager to the Reports page with the filters of the list in the form; nothing is requested', async () => {
    serveOrderList([reserved]);
    const exports = serveExports();
    const sent = serveExportRequests();
    const { user, location, store } = renderOrders(
      `/orders?channel=pos&status=Reserved&customerId=${AN_ID}&search=DP-2026&from=2026-09-01&to=2026-09-30&page=3&sortDir=asc`,
      managerUser,
    );
    await screen.findByText(reserved.externalOrderId);
    expect(exportButton()).toHaveStyle({ width: '176px' });

    await user.click(exportButton()!);

    await screen.findByRole('heading', { name: 'Báo cáo & xuất Excel' });
    // The filters travel in the location state: the address of the Reports page is its table's.
    expect(location()).toBe('/reports');
    const form = within(screen.getByTestId('orders-export-form'));
    expect(screen.getByTestId('orders-export-form')).toBeVisible();
    expect(form.getByPlaceholderText('Từ ngày')).toHaveValue('01/09/2026');
    expect(form.getByPlaceholderText('Đến ngày')).toHaveValue('30/09/2026');
    expect(form.getByLabelText('Tìm đơn hàng')).toHaveValue('DP-2026');
    expect(await form.findByText('POS')).toBeInTheDocument();
    expect(form.getByText('Đã giữ hàng')).toBeInTheDocument();
    expect(await form.findByText('Nguyễn Văn An')).toBeInTheDocument();
    await settled(store);
    // The table of the page asked for its first page; no export was requested.
    expect(exports.requests).toEqual([{ page: '1', pageSize: '20' }]);
    expect(sent.requests).toHaveLength(0);
  });

  it('hands over no range when the list has none: the form starts with its default range', async () => {
    serveOrderList([reserved]);
    serveExports();
    const sent = serveExportRequests();
    const { user, store } = renderOrders('/orders?status=InsufficientStock', managerUser);
    await screen.findByText(reserved.externalOrderId);

    await user.click(exportButton()!);

    await screen.findByRole('heading', { name: 'Báo cáo & xuất Excel' });
    const form = within(screen.getByTestId('orders-export-form'));
    const today = new Date();
    const twoDigits = (value: number) => String(value).padStart(2, '0');
    expect(form.getByPlaceholderText('Đến ngày')).toHaveValue(
      `${twoDigits(today.getDate())}/${twoDigits(today.getMonth() + 1)}/${today.getFullYear()}`,
    );
    expect(form.getByText('Không đủ hàng')).toBeInTheDocument();
    await settled(store);
    expect(sent.requests).toHaveLength(0);
  });

  it('is not offered to Staff', async () => {
    serveOrderList([reserved]);
    renderOrders('/orders', staffUser);
    await screen.findByText(reserved.externalOrderId);

    expect(exportButton()).not.toBeInTheDocument();
    await settled();
  });
});
