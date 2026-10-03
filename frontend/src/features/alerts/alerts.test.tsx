import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import { FLUSH_WINDOW_MS } from '@/features/realtime/hintTags';
import type { HubFactory } from '@/features/realtime/hub';
import {
  alert,
  insufficientAlert,
  LOW_STOCK_ALERT_ID,
  lowStockAlert,
  processingFailedAlert,
  serveAlerts,
  SYNC_ALERT_ID,
  syncFailedAlert,
} from '@/test/alerts';
import { chooseProduct, COFFEE_ID, coffeeProduct, serveProductCatalog, TEA_ID, teaProduct } from '@/test/catalog';
import { POS_ID, serveSummary } from '@/test/dashboard';
import { fakeHub } from '@/test/hub';
import {
  chooseOption,
  columnTexts,
  expectLanguageSwitchChangesTextOnly,
  findDialog,
  type AppUser,
} from '@/test/interactions';
import { ORDER_ID, serveChannels, TASK_ID } from '@/test/orders';
import { SYNC_TASK_ID, taskChannels } from '@/test/processing';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';
import type { Alert } from './alertsApi';

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

const ALL = [lowStockAlert, insufficientAlert, syncFailedAlert, processingFailedAlert];

/** The alert list with the endpoints of its channel filter and of its product picker. */
function serveList(items: Alert[] = ALL, totalCount = items.length) {
  const channels = serveChannels(taskChannels);
  const catalog = serveProductCatalog([teaProduct, coffeeProduct]);
  return { ...serveAlerts(items, totalCount), channels, catalog };
}

function renderAlerts(route = '/alerts', user: AuthUser = staffUser, createHub?: HubFactory) {
  return renderApp({ route, session: sessionOf(user), createHub });
}

/** The row of an alert (rows are keyed by the alert id). */
function rowOfAlert(alertId: string): HTMLElement {
  const row = document.querySelector<HTMLElement>(`tr[data-row-key="${alertId}"]`);
  if (!row) {
    throw new Error(`No row for alert ${alertId}`);
  }

  return row;
}

/** The cell of an alert's row in the column with this header. */
function cellOf(alertId: string, headerText: string): HTMLElement {
  const index = [...document.querySelectorAll('thead th')].findIndex((th) => th.textContent === headerText);
  const cell = rowOfAlert(alertId).querySelectorAll<HTMLElement>('td')[index];
  if (index < 0 || !cell) {
    throw new Error(`No column "${headerText}"`);
  }

  return cell;
}

const loaded = () => screen.findByText('channel_rejected');
const filter = (name: string) => screen.getByRole('combobox', { name });
const filterText = (name: string) => filter(name).closest('.ant-select')?.textContent ?? '';
const startInput = () => screen.getByPlaceholderText('Xảy ra từ ngày');
const endInput = () => screen.getByPlaceholderText('Đến ngày');
const sortOf = (name: RegExp) => screen.getByRole('columnheader', { name }).getAttribute('aria-sort');
const acknowledgeButtons = () => screen.queryAllByRole('button', { name: 'Ghi nhận' });
const importButtons = () => screen.queryAllByRole('button', { name: 'Nhập kho' });

const STATUS_DEFAULT = 'Chưa giải quyết (đang mở + đã ghi nhận)';
const CONFIRM_TITLE = 'Ghi nhận cảnh báo này?';

/** Clicks "Acknowledge" of an alert's row and confirms. */
async function acknowledge(user: AppUser, alertId: string) {
  await user.click(within(rowOfAlert(alertId)).getByRole('button', { name: 'Ghi nhận' }));
  const dialog = await findDialog(CONFIRM_TITLE);
  await user.click(within(dialog).getByRole('button', { name: 'Ghi nhận' }));
}

describe('alerts list', () => {
  it('asks for the default view without a status, and lists the alerts as the API sends them', async () => {
    const { requests } = serveList();
    renderAlerts();
    await loaded();

    // No status in the request: what "needs attention" means is the server's rule. The control says which that is.
    expect(requests).toEqual([{ params: { page: '1', pageSize: '20' }, status: [], type: [] }]);
    expect(filterText('Trạng thái')).toContain(STATUS_DEFAULT);
    // The API's default order (most recent occurrence first) is not asked for, and the table shows it.
    expect(sortOf(/Lần gần nhất/)).toBe('descending');

    expect(columnTexts('Loại')).toEqual(['Sắp hết hàng', 'Thiếu hàng', 'Lỗi đồng bộ kho', 'Lỗi xử lý']);
    expect(columnTexts('Trạng thái')).toEqual(['Đang mở', 'Đã ghi nhận', 'Đang mở', 'Đã giải quyết']);
    expect(columnTexts('Sản phẩm')).toEqual(['TEA-01 — Trà xanh', 'COF-01 — Cà phê', 'TEA-01 — Trà xanh', '—']);
    expect(columnTexts('Kênh bán')).toEqual(['—', '—', 'POS', '—']);
    expect(columnTexts('Lần gần nhất')).toEqual([
      '03/10/2026 09:30',
      '03/10/2026 09:00',
      '02/10/2026 09:30',
      '01/10/2026 08:05',
    ]);
    expect(columnTexts('Số lần')).toEqual(['1', '3', '1', '1']);
    expect(columnTexts('Lần đầu tiên')).toEqual([
      '03/10/2026 09:30',
      '02/10/2026 14:00',
      '02/10/2026 09:30',
      '01/10/2026 08:05',
    ]);
    expect(columnTexts('Ghi nhận bởi')).toEqual(['—', 'manager@example.com · 03/10/2026 09:10', '—', '—']);
    expect(columnTexts('Giải quyết lúc')).toEqual(['—', '—', '—', '01/10/2026 10:15']);

    // Type and status are a tag with an icon and a text: never colour alone.
    for (const header of ['Loại', 'Trạng thái']) {
      const tag = cellOf(LOW_STOCK_ALERT_ID, header).querySelector('.ant-tag');
      expect(tag?.querySelector('[role="img"]'), header).toBeInTheDocument();
      expect(tag?.textContent, header).not.toBe('');
    }
  });

  it('shows per type what the alert is about, with the links to the order, the task and the channel', async () => {
    serveList();
    const { user } = renderAlerts();
    await loaded();

    // Low stock: the product's numbers as the API sends them - they are the numbers of now, and the tooltip says so.
    const lowStock = cellOf(LOW_STOCK_ALERT_ID, 'Chi tiết');
    expect(lowStock).toHaveTextContent('Khả dụng 3 / ngưỡng 5');
    expect(lowStock.querySelectorAll('a')).toHaveLength(0);
    await user.hover(within(lowStock).getByText('Khả dụng 3 / ngưỡng 5'));
    expect(
      await screen.findByText('Số liệu hiện tại của sản phẩm, không phải tại thời điểm cảnh báo.'),
    ).toBeInTheDocument();

    // Insufficient stock: the latest order.
    const insufficient = within(cellOf(insufficientAlert.alertId, 'Chi tiết'));
    expect(insufficient.getByRole('link', { name: 'Đơn hàng' })).toHaveAttribute('href', `/orders/${ORDER_ID}`);
    expect(insufficient.queryByRole('link', { name: 'Tác vụ' })).not.toBeInTheDocument();

    // Stock-sync failure: the error code (as a code; the API has no message) and the latest task - no order.
    const sync = cellOf(SYNC_ALERT_ID, 'Chi tiết');
    expect(within(sync).getByText('channel_rejected').closest('code')).toBeInTheDocument();
    expect(within(sync).getByRole('link', { name: 'Tác vụ' })).toHaveAttribute(
      'href',
      `/processing/tasks/${SYNC_TASK_ID}`,
    );
    expect(within(sync).queryByRole('link', { name: 'Đơn hàng' })).not.toBeInTheDocument();
    expect(within(cellOf(SYNC_ALERT_ID, 'Kênh bán')).getByRole('link', { name: 'POS' })).toHaveAttribute(
      'href',
      `/channels/${POS_ID}`,
    );

    // Processing failure: the code, the task and - it has one - the order.
    const processing = within(cellOf(processingFailedAlert.alertId, 'Chi tiết'));
    expect(processing.getByText('db_constraint').closest('code')).toBeInTheDocument();
    expect(processing.getByRole('link', { name: 'Tác vụ' })).toHaveAttribute('href', `/processing/tasks/${TASK_ID}`);
    expect(processing.getByRole('link', { name: 'Đơn hàng' })).toHaveAttribute('href', `/orders/${ORDER_ID}`);
  });

  it('shows a dash where the API sends nothing', async () => {
    const bare = [
      alert({ alertId: 'a-low', type: 'LowStock' }),
      alert({ alertId: 'a-short', type: 'InsufficientStock' }),
      alert({ alertId: 'a-sync', type: 'StockSyncFailed' }),
      alert({ alertId: 'a-task', type: 'ProcessingFailed' }),
    ];
    serveList(bare);
    renderAlerts();
    await waitFor(() => expect(document.querySelectorAll('tbody tr.ant-table-row')).toHaveLength(4));

    for (const header of ['Sản phẩm', 'Kênh bán', 'Chi tiết', 'Ghi nhận bởi', 'Giải quyết lúc']) {
      expect(columnTexts(header), header).toEqual(['—', '—', '—', '—']);
    }

    expect(document.querySelectorAll('tbody a')).toHaveLength(0);
  });

  it('opens the order of an alert from its link', async () => {
    serveList();
    server.use(http.get(api('/orders/:id'), () => problem(404, { errorCode: 'order_not_found' })));
    const { user, location } = renderAlerts();
    await loaded();

    await user.click(within(cellOf(insufficientAlert.alertId, 'Chi tiết')).getByRole('link', { name: 'Đơn hàng' }));

    await waitFor(() => expect(location()).toBe(`/orders/${ORDER_ID}`));
    expect(await screen.findByText('Không tìm thấy đơn hàng')).toBeInTheDocument();
  });

  it.each([
    ['Staff: no action column', staffUser, false],
    ['Manager: with the action column', managerUser, true],
  ] as const)(
    'keeps what an alert is and what can be done about it inside a 1280 px window (%s)',
    async (_name, user, hasActions) => {
      serveList();
      renderAlerts('/alerts', user);
      await loaded();

      const headers = [...document.querySelectorAll('thead th')].map((th) => th.textContent);
      expect(headers.slice(0, 6)).toEqual(['Loại', 'Trạng thái', 'Sản phẩm', 'Kênh bán', 'Chi tiết', 'Lần gần nhất']);
      expect(headers.at(-1)).toBe(hasActions ? 'Thao tác' : 'Giải quyết lúc');

      // jsdom has no layout: the fixed column widths are what can be checked. 1280 px window - 240 px menu - 2 × 24 px
      // padding = 992 px, minus a vertical scrollbar. The action column is fixed to the right edge of that area.
      const widths = [...document.querySelectorAll<HTMLElement>('table colgroup col')].map((col) =>
        Number.parseInt(col.style.width, 10),
      );
      expect(widths).toHaveLength(headers.length);
      expect(widths.every((width) => width > 0)).toBe(true);
      const firstSix = widths.slice(0, 6).reduce((sum, width) => sum + width, 0);
      expect(firstSix + (hasActions ? widths.at(-1)! : 0)).toBeLessThanOrEqual(976);
    },
  );

  it('restores every filter, the sort and the page from the address (reload)', async () => {
    const { requests, catalog } = serveList([syncFailedAlert], 120);
    const address =
      `/alerts?status=Open&status=Resolved&type=StockSyncFailed&type=LowStock&productId=${TEA_ID}&channelId=${POS_ID}` +
      `&from=2026-10-01&to=2026-10-02&sortBy=occurrenceCount&sortDir=asc&page=2&pageSize=50`;
    const { location } = renderAlerts(address);
    await loaded();

    expect(requests).toHaveLength(1);
    const { from, to, ...rest } = requests[0]!.params;
    expect(rest).toEqual({
      page: '2',
      pageSize: '50',
      sortBy: 'occurrenceCount',
      sortDir: 'asc',
      productId: TEA_ID,
      channelId: POS_ID,
    });
    // One parameter per value, in the order of the API's enums whatever the order in the address.
    expect(requests[0]!.status).toEqual(['Open', 'Resolved']);
    expect(requests[0]!.type).toEqual(['LowStock', 'StockSyncFailed']);
    // The local start of the first day and the local end of the last day, written with the UTC offset.
    expect(from).toMatch(/^2026-10-01T00:00:00[+-]\d{2}:\d{2}$/);
    expect(new Date(from!).getTime()).toBe(new Date(2026, 9, 1).getTime());
    expect(to).toMatch(/^2026-10-02T23:59:59\.999[+-]\d{2}:\d{2}$/);
    expect(new Date(to!).getTime()).toBe(new Date(2026, 9, 2, 23, 59, 59, 999).getTime());

    // The controls show the state of the address.
    expect(filterText('Trạng thái')).toContain('Đang mở');
    expect(filterText('Trạng thái')).toContain('Đã giải quyết');
    expect(filterText('Trạng thái')).not.toContain(STATUS_DEFAULT);
    expect(filterText('Loại cảnh báo')).toContain('Sắp hết hàng');
    expect(filterText('Loại cảnh báo')).toContain('Lỗi đồng bộ kho');
    await waitFor(() => expect(filterText('Sản phẩm')).toContain('TEA-01 — Trà xanh'));
    expect(catalog.detailRequests).toEqual([TEA_ID]);
    await waitFor(() => expect(filterText('Kênh bán')).toContain('POS'));
    expect(startInput()).toHaveValue('01/10/2026');
    expect(endInput()).toHaveValue('02/10/2026');
    expect(document.querySelector('.ant-pagination-item-active')).toHaveTextContent('2');
    expect(sortOf(/Số lần/)).toBe('ascending');
    expect(sortOf(/Lần gần nhất/)).toBeNull();
    expect(location()).toBe(address);
  });

  describe('filters', () => {
    /** The list (45 alerts: three pages) opened with `address`, once its first page is shown. */
    async function renderFiltered(address: string) {
      const { requests } = serveList(ALL, 45);
      const app = renderAlerts(address);
      await loaded();
      const secondPage = async () => {
        await app.user.click(screen.getByRole('listitem', { name: '2' }));
        await waitFor(() => expect(requests.at(-1)?.params).toMatchObject({ page: '2' }));
        // The page is part of the address, next to the filters that are set.
        expect(app.location()).toMatch(/[?&]page=2$/);
      };
      return { ...app, requests, secondPage };
    }

    it('status: "Resolved" and "all" are explicit choices; each change starts on page 1 again', async () => {
      const { user, location, requests } = await renderFiltered('/alerts?page=3');
      expect(requests).toEqual([{ params: { page: '3', pageSize: '20' }, status: [], type: [] }]);

      await chooseOption(user, filter('Trạng thái'), 'Đã giải quyết');
      await waitFor(() => expect(requests.at(-1)?.status).toEqual(['Resolved']));
      expect(requests.at(-1)?.params).toEqual({ page: '1', pageSize: '20' });
      expect(location()).toBe('/alerts?status=Resolved');

      // "All" is every status chosen - sent as such, in the order of the API's enum.
      await chooseOption(user, filter('Trạng thái'), 'Đang mở');
      await waitFor(() => expect(requests.at(-1)?.status).toEqual(['Open', 'Resolved']));
      await chooseOption(user, filter('Trạng thái'), 'Đã ghi nhận');
      await waitFor(() => expect(requests.at(-1)?.status).toEqual(['Open', 'Acknowledged', 'Resolved']));
      expect(location()).toBe('/alerts?status=Open&status=Acknowledged&status=Resolved');
    });

    it('type, product and channel go to the address next to each other and start on page 1 again', async () => {
      const { user, location, requests, secondPage } = await renderFiltered('/alerts?page=3');

      await chooseOption(user, filter('Loại cảnh báo'), 'Lỗi đồng bộ kho');
      await waitFor(() => expect(requests.at(-1)?.type).toEqual(['StockSyncFailed']));
      expect(requests.at(-1)?.params).toEqual({ page: '1', pageSize: '20' });
      expect(requests.at(-1)?.status).toEqual([]);
      expect(location()).toBe('/alerts?type=StockSyncFailed');

      await secondPage();
      await chooseProduct(user, filter('Sản phẩm'), 'COF-01 — Cà phê');
      await waitFor(() => expect(requests.at(-1)?.params).toEqual({ page: '1', pageSize: '20', productId: COFFEE_ID }));

      await secondPage();
      await chooseOption(user, filter('Kênh bán'), 'POS');
      await waitFor(() => expect(requests.at(-1)?.params).toMatchObject({ page: '1', channelId: POS_ID }));
      expect(requests.at(-1)?.type).toEqual(['StockSyncFailed']);
      expect(location()).toBe(`/alerts?type=StockSyncFailed&productId=${COFFEE_ID}&channelId=${POS_ID}`);
    });

    it('filters by a range of local days on the last occurrence and starts on page 1 again', async () => {
      const { user, location, requests } = await renderFiltered('/alerts?status=Resolved&page=2');

      await user.click(startInput());
      await user.type(startInput(), '01/10/2026{Enter}');
      await user.type(endInput(), '02/10/2026{Enter}');
      await waitFor(() => expect(requests.at(-1)?.params.to).toBeDefined());
      expect(requests.at(-1)?.params).toMatchObject({ page: '1' });
      expect(requests.at(-1)?.status).toEqual(['Resolved']);
      expect(new Date(requests.at(-1)!.params.from!).getTime()).toBe(new Date(2026, 9, 1).getTime());
      expect(new Date(requests.at(-1)!.params.to!).getTime()).toBe(new Date(2026, 9, 2, 23, 59, 59, 999).getTime());
      expect(location()).toBe('/alerts?status=Resolved&from=2026-10-01&to=2026-10-02');
    });

    it('"Clear filters" removes every filter from the address and from the controls; the sort stays', async () => {
      const { user, location, requests } = await renderFiltered(
        `/alerts?status=Resolved&type=LowStock&productId=${TEA_ID}&channelId=${POS_ID}` +
          `&from=2026-10-01&to=2026-10-02&sortBy=firstOccurredAt&sortDir=desc&page=2`,
      );
      await waitFor(() => expect(filterText('Kênh bán')).toContain('POS'));

      await user.click(screen.getByRole('button', { name: 'Xoá bộ lọc' }));
      await waitFor(() =>
        expect(requests.at(-1)).toEqual({
          params: { page: '1', pageSize: '20', sortBy: 'firstOccurredAt', sortDir: 'desc' },
          status: [],
          type: [],
        }),
      );
      expect(location()).toBe('/alerts?sortBy=firstOccurredAt&sortDir=desc');
      expect(filterText('Trạng thái')).toContain(STATUS_DEFAULT);
      expect(filterText('Loại cảnh báo')).not.toContain('Sắp hết hàng');
      expect(filterText('Sản phẩm')).not.toContain('TEA-01');
      expect(filterText('Kênh bán')).not.toContain('POS');
      expect(startInput()).toHaveValue('');
      expect(endInput()).toHaveValue('');
    });
  });

  it("sorts through the address by the server's three fields, each most recent / most frequent first", async () => {
    const { requests } = serveList(ALL, 45);
    const { user, location } = renderAlerts('/alerts?page=2&status=Resolved');
    await loaded();

    // The default is the most recent occurrence first, so the first click on it asks for the oldest first.
    await user.click(screen.getByRole('columnheader', { name: /Lần gần nhất/ }));
    await waitFor(() =>
      expect(requests.at(-1)?.params).toEqual({ page: '1', pageSize: '20', sortBy: 'lastOccurredAt', sortDir: 'asc' }),
    );
    expect(requests.at(-1)?.status).toEqual(['Resolved']);
    expect(location()).toBe('/alerts?status=Resolved&sortDir=asc');

    await user.click(screen.getByRole('columnheader', { name: /Số lần/ }));
    await waitFor(() => expect(requests.at(-1)?.params).toMatchObject({ sortBy: 'occurrenceCount', sortDir: 'desc' }));
    expect(location()).toBe('/alerts?status=Resolved&sortDir=desc&sortBy=occurrenceCount');
    expect(sortOf(/Số lần/)).toBe('descending');
    expect(sortOf(/Lần gần nhất/)).toBeNull();

    await user.click(screen.getByRole('columnheader', { name: /Lần đầu tiên/ }));
    await waitFor(() => expect(requests.at(-1)?.params).toMatchObject({ sortBy: 'firstOccurredAt', sortDir: 'desc' }));
    expect(location()).toBe('/alerts?status=Resolved&sortDir=desc&sortBy=firstOccurredAt');
  });

  it('ignores address values that are not valid and never sends them', async () => {
    const { requests } = serveList();
    renderAlerts(
      '/alerts?status=Done&status=Open,Resolved&type=Nope&productId=tea&channelId=pos&from=2026-13-45&to=soon&sortBy=secret&page=x',
    );
    await loaded();

    expect(requests).toEqual([{ params: { page: '1', pageSize: '20' }, status: [], type: [] }]);
    expect(filterText('Trạng thái')).toContain(STATUS_DEFAULT);
    expect(startInput()).toHaveValue('');
    expect(sortOf(/Lần gần nhất/)).toBe('descending');
  });

  it("shows the server's rejection of the range on the range control, without rows, and recovers", async () => {
    const { state, requests } = serveList();
    const tooLong = "Khoảng thời gian từ 'Từ ngày' không được dài quá 92 ngày.";
    state.answer = () => problem(400, { errorCode: 'validation_failed', errors: { to: [tooLong] } });
    const { user } = renderAlerts('/alerts?from=2026-01-01&to=2026-09-30');

    expect(await screen.findByTestId('alerts-range-error')).toHaveTextContent(tooLong);
    expect(startInput().closest('.ant-picker')).toHaveClass('ant-picker-status-error');
    // The limit is the server's: the range was sent as it is, once.
    expect(requests).toHaveLength(1);
    expect(document.querySelectorAll('tbody tr.ant-table-row')).toHaveLength(0);
    // Said once, on the control - not again above the table, and not as an unexpected error.
    expect(screen.getAllByText(tooLong)).toHaveLength(1);
    expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();

    state.answer = undefined;
    await user.click(screen.getByRole('button', { name: /Làm mới/ }));
    await loaded();
    expect(screen.queryByTestId('alerts-range-error')).not.toBeInTheDocument();
    expect(startInput().closest('.ant-picker')).not.toHaveClass('ant-picker-status-error');
  });

  it('shows any other rejection above the table', async () => {
    const { state } = serveList();
    state.answer = () => problem(403);
    renderAlerts();

    expect(await screen.findByRole('alert')).toHaveTextContent('Bạn không có quyền thực hiện thao tác này.');
    expect(screen.queryByTestId('alerts-range-error')).not.toBeInTheDocument();
    expect(document.querySelectorAll('tbody tr.ant-table-row')).toHaveLength(0);
  });

  it('asks again on "Refresh" and whenever the page is opened', async () => {
    const { requests } = serveList();
    const { user } = renderAlerts();
    await loaded();

    await user.click(screen.getByRole('button', { name: /Làm mới/ }));
    await waitFor(() => expect(requests).toHaveLength(2));

    await user.click(screen.getByRole('link', { name: 'Báo cáo' }));
    await screen.findByText('Trang này sẽ có ở task 021.');
    await user.click(screen.getByRole('link', { name: 'Cảnh báo' }));
    await loaded();
    await waitFor(() => expect(requests).toHaveLength(3));
  });
});

describe('alerts list: actions', () => {
  it.each([
    ['Staff', staffUser, 0, 0],
    ['Manager', managerUser, 2, 2],
    ['Admin', adminUser, 2, 2],
  ] as const)('%s sees %i acknowledge and %i import actions', async (_role, user, acknowledges, imports) => {
    serveList();
    renderAlerts('/alerts', user);
    await loaded();

    // Acknowledge: on Open rows only (low stock, sync failure). Import: unresolved stock alerts (low, insufficient).
    expect(acknowledgeButtons()).toHaveLength(acknowledges);
    expect(importButtons()).toHaveLength(imports);
    expect(screen.queryAllByRole('columnheader', { name: 'Thao tác' })).toHaveLength(acknowledges > 0 ? 1 : 0);
    // The alerts themselves are visible to every role.
    expect(document.querySelectorAll('tbody tr.ant-table-row')).toHaveLength(4);
  });

  it('offers acknowledge on Open rows only, and the stock import on unresolved stock alerts only', async () => {
    const resolvedLow = alert({ ...lowStockAlert, alertId: 'a-resolved-low', status: 'Resolved' });
    serveList([...ALL, resolvedLow]);
    renderAlerts('/alerts', managerUser);
    await loaded();

    const actionsOf = (alertId: string) =>
      within(rowOfAlert(alertId))
        .queryAllByRole('button')
        .map((button) => button.getAttribute('aria-label'));
    expect(actionsOf(LOW_STOCK_ALERT_ID)).toEqual(['Ghi nhận', 'Nhập kho']);
    // Acknowledged: nothing to acknowledge; still a stock alert that is not resolved.
    expect(actionsOf(insufficientAlert.alertId)).toEqual(['Nhập kho']);
    expect(actionsOf(SYNC_ALERT_ID)).toEqual(['Ghi nhận']);
    expect(actionsOf(processingFailedAlert.alertId)).toEqual([]);
    expect(actionsOf('a-resolved-low')).toEqual([]);
  });

  it('opens the stock import with the product of the alert', async () => {
    serveList();
    const { user, location } = renderAlerts('/alerts', managerUser);
    await loaded();

    await user.click(within(rowOfAlert(insufficientAlert.alertId)).getByRole('button', { name: 'Nhập kho' }));

    expect(await screen.findByRole('heading', { name: 'Nhập kho' })).toBeInTheDocument();
    expect(location()).toBe(`/inventory/import?productId=${COFFEE_ID}`);
    expect(await screen.findByText('COF-01 — Cà phê')).toBeInTheDocument();
  });

  it('asks before acknowledging; cancelling sends nothing', async () => {
    const { acknowledges, requests } = serveList();
    const { user } = renderAlerts('/alerts', managerUser);
    await loaded();

    await user.click(within(rowOfAlert(LOW_STOCK_ALERT_ID)).getByRole('button', { name: 'Ghi nhận' }));
    const dialog = await findDialog(CONFIRM_TITLE);
    expect(dialog).toHaveTextContent('không thể hoàn tác');
    await user.click(within(dialog).getByRole('button', { name: 'Huỷ' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(acknowledges).toEqual([]);
    expect(requests).toHaveLength(1);
    expect(cellOf(LOW_STOCK_ALERT_ID, 'Trạng thái')).toHaveTextContent('Đang mở');
  });

  it('acknowledges after the confirmation: the list is fetched again and the row shows who and when', async () => {
    const { acknowledges, requests } = serveList();
    const summaries = serveSummary();
    const { user } = renderAlerts('/alerts', managerUser);
    await loaded();

    await acknowledge(user, LOW_STOCK_ALERT_ID);

    expect(await screen.findByText('Đã ghi nhận cảnh báo.')).toBeInTheDocument();
    expect(acknowledges).toEqual([LOW_STOCK_ALERT_ID]);
    await waitFor(() => expect(cellOf(LOW_STOCK_ALERT_ID, 'Trạng thái')).toHaveTextContent('Đã ghi nhận'));
    expect(cellOf(LOW_STOCK_ALERT_ID, 'Ghi nhận bởi')).toHaveTextContent('manager@example.com · 03/10/2026 10:00');
    expect(requests).toHaveLength(2);
    // It is acknowledged now: the action is gone, the stock import stays.
    expect(within(rowOfAlert(LOW_STOCK_ALERT_ID)).queryByRole('button', { name: 'Ghi nhận' })).not.toBeInTheDocument();
    expect(within(rowOfAlert(LOW_STOCK_ALERT_ID)).getByRole('button', { name: 'Nhập kho' })).toBeInTheDocument();
    // The dashboard is not on screen: its summary is not asked for.
    expect(summaries.requests).toHaveLength(0);
  });

  it.each([
    [409, 'alert_resolved', 'Cảnh báo đã được hệ thống giải quyết nên không thể ghi nhận.'],
    [404, 'alert_not_found', 'Cảnh báo không còn tồn tại.'],
  ] as const)(
    'says in the UI language that the API refused (%i %s) and fetches the list again',
    async (status, errorCode, text) => {
      const { state, requests } = serveList();
      state.acknowledgeAnswer = () => {
        // What the list on screen did not know yet.
        state.items = [insufficientAlert, syncFailedAlert, processingFailedAlert];
        return problem(status, { errorCode });
      };
      const { user } = renderAlerts('/alerts', managerUser);
      await loaded();

      await acknowledge(user, LOW_STOCK_ALERT_ID);

      expect(await screen.findByText(text)).toBeInTheDocument();
      await waitFor(() => expect(document.querySelectorAll('tbody tr.ant-table-row')).toHaveLength(3));
      expect(requests).toHaveLength(2);
      expect(screen.queryByText('Đã ghi nhận cảnh báo.')).not.toBeInTheDocument();
      // Not reported as an unexpected error.
      expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();
    },
  );

  it('leaves the list alone when the API refuses for another reason (403)', async () => {
    const { state, requests } = serveList();
    state.acknowledgeAnswer = () => problem(403);
    const { user } = renderAlerts('/alerts', managerUser);
    await loaded();

    await acknowledge(user, LOW_STOCK_ALERT_ID);

    expect(await screen.findByText('Bạn không có quyền thực hiện thao tác này.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await pause(200);
    expect(requests).toHaveLength(1);
    expect(cellOf(LOW_STOCK_ALERT_ID, 'Trạng thái')).toHaveTextContent('Đang mở');
  });
});

describe('alerts list: realtime', () => {
  /** The list on screen with a live connection, once it is idle (see `realtime.test.tsx`). */
  async function renderLive() {
    const backend = serveList();
    const hub = fakeHub({ autoAccept: true });
    const app = renderAlerts('/alerts', staffUser, hub.create);
    await loaded();
    await waitFor(() => expect(screen.getByTestId('connection-indicator')).toHaveTextContent('Trực tiếp'));
    // The page's own request and, when the page was there before the connection, the one every new connection causes.
    await pause(FLUSH_WINDOW_MS + 300);
    return { ...app, ...backend, hub, loadedRequests: backend.requests.length };
  }

  it('is refetched by a hint about an alert: the new row appears without a reload, and no toast is shown', async () => {
    const { state, requests, hub, loadedRequests } = await renderLive();

    const fresh = alert({ ...lowStockAlert, alertId: 'a-new', productSku: 'COF-01', productName: 'Cà phê' });
    state.items = [fresh, ...ALL];
    // As the server sends them for a new low-stock alert: both events in one batch.
    await hub.latest().send([
      { type: 'alert.changed', alertId: 'a-new', alertType: 'LowStock', status: 'Open', productId: COFFEE_ID },
      { type: 'low_stock.created', alertId: 'a-new', productId: COFFEE_ID },
    ]);

    await waitFor(() => expect(document.querySelectorAll('tbody tr.ant-table-row')).toHaveLength(5));
    expect(requests).toHaveLength(loadedRequests + 1);
    expect(document.querySelector('.ant-message-notice, .ant-notification-notice')).toBeNull();
  });

  it('is refetched by a stock change (a low-stock row shows the stock of now), and not by hints about other things', async () => {
    const { state, requests, hub, loadedRequests } = await renderLive();

    state.items = [{ ...lowStockAlert, availableStock: 1 }, insufficientAlert, syncFailedAlert, processingFailedAlert];
    await hub.latest().send([{ type: 'inventory.changed', productIds: [TEA_ID] }]);
    await waitFor(() => expect(cellOf(LOW_STOCK_ALERT_ID, 'Chi tiết')).toHaveTextContent('Khả dụng 1 / ngưỡng 5'));
    expect(requests).toHaveLength(loadedRequests + 1);

    await pause(FLUSH_WINDOW_MS + 300);
    await hub.latest().send([{ type: 'order.created', orderId: ORDER_ID }]);
    await hub.latest().send([{ type: 'processing.task.changed', taskId: TASK_ID, taskType: 'OrderProcessing' }]);
    await pause(FLUSH_WINDOW_MS + 300);
    expect(requests).toHaveLength(loadedRequests + 1);
  });

  it('asks for nothing on an alert hint while neither the list nor the dashboard is on screen', async () => {
    const { requests } = serveList();
    const summaries = serveSummary();
    const hub = fakeHub({ autoAccept: true });
    renderApp({ route: '/reports', session: sessionOf(staffUser), createHub: hub.create });
    await screen.findByText('Trang này sẽ có ở task 021.');
    await waitFor(() => expect(screen.getByTestId('connection-indicator')).toHaveTextContent('Trực tiếp'));
    await pause(FLUSH_WINDOW_MS + 300);

    await hub.latest().send([
      { type: 'alert.changed', alertId: LOW_STOCK_ALERT_ID, alertType: 'LowStock', status: 'Resolved' },
      { type: 'low_stock.created', alertId: 'a-new', productId: TEA_ID },
      { type: 'inventory.changed', productIds: [TEA_ID] },
    ]);
    await pause(FLUSH_WINDOW_MS + 300);

    expect(requests).toHaveLength(0);
    expect(summaries.requests).toHaveLength(0);
  });
});

describe('alerts list: cache', () => {
  it('is fetched again after a product or a channel was changed (their names and the threshold are shown)', async () => {
    const { requests } = serveList();
    const { store } = renderAlerts('/alerts', managerUser);
    await loaded();
    const { productsApi } = await import('@/features/products/productsApi');
    const { channelsApi } = await import('@/features/channels/channelsApi');

    server.use(
      http.put(api('/products/:id'), () => HttpResponse.json(teaProduct)),
      http.put(api('/channels/:id'), () => HttpResponse.json(taskChannels[1])),
    );
    const changes = { name: 'Trà xanh', price: 10000, lowStockThreshold: 9, expiryDate: null };
    await store.dispatch(productsApi.endpoints.updateProduct.initiate({ productId: TEA_ID, changes }));
    await waitFor(() => expect(requests).toHaveLength(2));

    await store.dispatch(
      channelsApi.endpoints.updateChannel.initiate({ channelId: POS_ID, changes: { name: 'POS 2', status: 'Active' } }),
    );
    await waitFor(() => expect(requests).toHaveLength(3));

    // A rejected change changed nothing on the server: nothing is fetched again.
    server.use(http.put(api('/products/:id'), () => problem(409, { errorCode: 'concurrency_conflict' })));
    await store.dispatch(productsApi.endpoints.updateProduct.initiate({ productId: TEA_ID, changes }));
    await pause(200);
    expect(requests).toHaveLength(3);
  });
});

describe('alerts list: language switch', () => {
  it('changes text only: address, filters, their controls, the page and the open confirmation stay', async () => {
    const { requests, channels, catalog } = serveList(ALL, 45);
    const address =
      `/alerts?status=Open&status=Acknowledged&type=LowStock&type=StockSyncFailed&productId=${TEA_ID}` +
      `&channelId=${POS_ID}&from=2026-10-01&to=2026-10-03&sortBy=occurrenceCount&sortDir=desc&page=2`;
    const { user, location } = renderAlerts(address, managerUser);
    await loaded();
    await waitFor(() => expect(filterText('Kênh bán')).toContain('POS'));
    await waitFor(() => expect(filterText('Sản phẩm')).toContain('TEA-01 — Trà xanh'));

    await user.click(within(rowOfAlert(LOW_STOCK_ALERT_ID)).getByRole('button', { name: 'Ghi nhận' }));
    await findDialog(CONFIRM_TITLE);

    await expectLanguageSwitchChangesTextOnly(user, location, {
      vietnamese: ['Cảnh báo', 'Sắp hết hàng', 'Đã ghi nhận', 'Khả dụng 3 / ngưỡng 5', 'Lần gần nhất', 'Xoá bộ lọc'],
      english: ['Alerts', 'Low stock', 'Acknowledged', 'Available 3 / threshold 5', 'Last occurred', 'Clear filters'],
      requestCount: () =>
        requests.length + channels.count + catalog.listRequests.length + catalog.detailRequests.length,
    });

    expect(columnTexts('Lần gần nhất')).toEqual([
      '03/10/2026 09:30',
      '03/10/2026 09:00',
      '02/10/2026 09:30',
      '01/10/2026 08:05',
    ]);
    expect(startInput()).toHaveValue('01/10/2026');
    expect(endInput()).toHaveValue('03/10/2026');
    expect(location()).toBe(address);
    // The confirmation is still the one that was opened; cancelling it sends nothing.
    await user.click(within(await findDialog(CONFIRM_TITLE)).getByRole('button', { name: 'Huỷ' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(requests).toHaveLength(1);
  });
});
