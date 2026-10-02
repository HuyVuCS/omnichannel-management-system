import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import { FLUSH_WINDOW_MS } from '@/features/realtime/hintTags';
import type { HubFactory } from '@/features/realtime/hub';
import { POS_ID, WEBSITE_ID } from '@/test/dashboard';
import { fakeHub } from '@/test/hub';
import { chooseOption, columnTexts, expectLanguageSwitchChangesTextOnly, rowOf } from '@/test/interactions';
import { ORDER_ID, orderDetail, serveChannels, TASK_ID } from '@/test/orders';
import {
  failedSync,
  failedSyncDetail,
  serveTask,
  serveTaskList,
  SYNC_TASK_ID,
  taskChannels,
  taskSummary,
  UNKNOWN_CHANNEL_ID,
} from '@/test/processing';
import { renderApp } from '@/test/renderApp';
import { api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

const orderTask = taskSummary();
const unknownChannelSync = taskSummary({
  taskId: '0199a1b2-0000-7000-8000-0000000000cc',
  taskType: 'StockSync',
  status: 'RetryScheduled',
  orderId: null,
  channelId: UNKNOWN_CHANNEL_ID,
  productSku: 'COF-01',
  createdAt: new Date(2026, 8, 30, 10, 15).toISOString(),
  updatedAt: null,
  attemptCount: 0,
  lastAttempt: null,
});

function serveList(items = [failedSync, orderTask, unknownChannelSync], totalCount = items.length) {
  const channels = serveChannels(taskChannels);
  return { ...serveTaskList(items, totalCount), channels };
}

function renderProcessing(route = '/processing', user: AuthUser = staffUser, createHub?: HubFactory) {
  return renderApp({ route, session: sessionOf(user), createHub });
}

const filter = (name: string) => screen.getByRole('combobox', { name });
const startInput = () => screen.getByPlaceholderText('Tạo từ ngày');
const endInput = () => screen.getByPlaceholderText('Đến ngày');
const sortOf = (name: RegExp) => screen.getByRole('columnheader', { name }).getAttribute('aria-sort');

describe('processing list', () => {
  it('lists tasks as the API sends them: status, channel name, product SKU, order link and the last attempt', async () => {
    const { requests } = serveList();
    renderProcessing();

    expect(await screen.findByText('channel_rejected')).toBeInTheDocument();
    // Nothing in the address: the API's default order (newest first) is not asked for, and the table shows it.
    expect(requests).toEqual([{ page: '1', pageSize: '20' }]);
    expect(sortOf(/Ngày tạo/)).toBe('descending');

    expect(columnTexts('Ngày tạo')).toEqual(['02/10/2026 09:30', '01/10/2026 08:05', '30/09/2026 10:15']);
    expect(columnTexts('Loại tác vụ')).toEqual(['Đồng bộ tồn kho', 'Xử lý đơn hàng', 'Đồng bộ tồn kho']);
    expect(columnTexts('Trạng thái')).toEqual(['Thất bại', 'Thành công', 'Chờ thử lại']);
    // The channel's name comes from the channel list; a channel it does not contain is shown by its id.
    await waitFor(() => expect(columnTexts('Kênh bán')).toEqual(['POS', '', UNKNOWN_CHANNEL_ID]));
    expect(columnTexts('SKU')).toEqual(['TEA-01', '', 'COF-01']);
    expect(columnTexts('Số lần chạy')).toEqual(['2', '1', '0']);
    expect(columnTexts('Mã lỗi gần nhất')).toEqual(['channel_rejected', '', '']);
    expect(columnTexts('Phiên bản worker')).toEqual(['StockSyncWorker@host-1:4242', 'order-worker@host-1:4242', '']);
    expect(columnTexts('Thông báo lỗi')).toEqual(['The channel rejected the stock update.', '', '']);
    expect(columnTexts('Cập nhật lúc')).toEqual(['02/10/2026 09:30', '01/10/2026 08:05', '']);

    // One status colour per status, as on every other page.
    expect(within(rowOf('channel_rejected')).getByText('Thất bại').closest('.ant-tag')).toHaveClass('ant-tag-error');
    // Only a task with an order links to it.
    expect(screen.getAllByRole('link', { name: 'Mở đơn' })).toHaveLength(1);
    expect(screen.getByRole('link', { name: 'Mở đơn' })).toHaveAttribute('href', `/orders/${ORDER_ID}`);
  });

  it('keeps status, channel and product inside the content area of a 1280 px window', async () => {
    serveList();
    renderProcessing();
    await screen.findByText('channel_rejected');

    const headers = [...document.querySelectorAll('thead th')].map((th) => th.textContent);
    expect(headers.slice(0, 5)).toEqual(['Ngày tạo', 'Loại tác vụ', 'Trạng thái', 'Kênh bán', 'SKU']);
    // jsdom has no layout: the fixed column widths are what can be checked (1280 - 240 menu - 2 × 24 padding = 992).
    const widths = [...document.querySelectorAll<HTMLElement>('table colgroup col')].map((col) =>
      Number.parseInt(col.style.width, 10),
    );
    expect(widths.slice(0, 5).reduce((sum, width) => sum + width, 0)).toBeLessThanOrEqual(992);
  });

  it('restores every filter, the sort and the page from the address (reload)', async () => {
    const { requests } = serveList([failedSync], 120);
    const address =
      `/processing?taskType=StockSync&status=Failed&channelId=${POS_ID}&correlationId=corr-sync-7` +
      `&errorCode=channel_rejected&from=2026-10-01&to=2026-10-02&sortBy=updatedAt&sortDir=asc&page=2&pageSize=50`;
    const { location } = renderProcessing(address);

    expect(await screen.findByText('channel_rejected')).toBeInTheDocument();
    expect(requests).toHaveLength(1);
    const { from, to, ...rest } = requests[0]!;
    expect(rest).toEqual({
      page: '2',
      pageSize: '50',
      sortBy: 'updatedAt',
      sortDir: 'asc',
      taskType: 'StockSync',
      status: 'Failed',
      channelId: POS_ID,
      correlationId: 'corr-sync-7',
      errorCode: 'channel_rejected',
    });
    // The local start of the first day and the local end of the last day, written with the UTC offset.
    expect(from).toMatch(/^2026-10-01T00:00:00[+-]\d{2}:\d{2}$/);
    expect(new Date(from!).getTime()).toBe(new Date(2026, 9, 1).getTime());
    expect(to).toMatch(/^2026-10-02T23:59:59\.999[+-]\d{2}:\d{2}$/);
    expect(new Date(to!).getTime()).toBe(new Date(2026, 9, 2, 23, 59, 59, 999).getTime());

    // The controls show the state of the address.
    expect(filter('Loại tác vụ').closest('.ant-select')).toHaveTextContent('Đồng bộ tồn kho');
    expect(filter('Trạng thái').closest('.ant-select')).toHaveTextContent('Thất bại');
    await waitFor(() => expect(filter('Kênh bán').closest('.ant-select')).toHaveTextContent('POS'));
    expect(screen.getByRole('textbox', { name: 'Mã tương quan' })).toHaveValue('corr-sync-7');
    expect(screen.getByRole('textbox', { name: 'Mã lỗi' })).toHaveValue('channel_rejected');
    expect(startInput()).toHaveValue('01/10/2026');
    expect(endInput()).toHaveValue('02/10/2026');
    expect(document.querySelector('.ant-pagination-item-active')).toHaveTextContent('2');
    expect(sortOf(/Cập nhật lúc/)).toBe('ascending');
    expect(sortOf(/Ngày tạo/)).toBeNull();
    expect(location()).toBe(address);
  });

  it('writes each filter to the address, starts on page 1 again, and "Clear filters" removes them', async () => {
    const { requests } = serveList([failedSync, orderTask], 45);
    const { user, location } = renderProcessing('/processing?page=3');
    await screen.findByText('channel_rejected');
    expect(requests).toEqual([{ page: '3', pageSize: '20' }]);
    const secondPage = async () => {
      await user.click(screen.getByRole('listitem', { name: '2' }));
      await waitFor(() => expect(requests.at(-1)).toMatchObject({ page: '2' }));
    };

    await chooseOption(user, filter('Loại tác vụ'), 'Đồng bộ tồn kho');
    await waitFor(() => expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', taskType: 'StockSync' }));
    expect(location()).toBe('/processing?taskType=StockSync');

    await secondPage();
    await chooseOption(user, filter('Trạng thái'), 'Thất bại');
    await waitFor(() =>
      expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', taskType: 'StockSync', status: 'Failed' }),
    );

    await secondPage();
    await chooseOption(user, filter('Kênh bán'), 'POS');
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ page: '1', channelId: POS_ID }));
    expect(location()).toBe(`/processing?taskType=StockSync&status=Failed&channelId=${POS_ID}`);

    // Text filters: one request per pause in typing, and the first page.
    await secondPage();
    let before = requests.length;
    await user.type(screen.getByRole('textbox', { name: 'Mã tương quan' }), 'corr-sync-7');
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ correlationId: 'corr-sync-7', page: '1' }));
    expect(requests.length).toBe(before + 1);

    await secondPage();
    before = requests.length;
    await user.type(screen.getByRole('textbox', { name: 'Mã lỗi' }), 'channel_rejected');
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ errorCode: 'channel_rejected', page: '1' }));
    expect(requests.length).toBe(before + 1);

    // A range of local days on the creation time.
    await secondPage();
    await user.click(startInput());
    await user.type(startInput(), '01/10/2026{Enter}');
    await user.type(endInput(), '02/10/2026{Enter}');
    await waitFor(() => expect(requests.at(-1)?.to).toBeDefined());
    expect(requests.at(-1)).toMatchObject({ page: '1' });
    expect(new Date(requests.at(-1)!.from!).getTime()).toBe(new Date(2026, 9, 1).getTime());
    expect(new Date(requests.at(-1)!.to!).getTime()).toBe(new Date(2026, 9, 2, 23, 59, 59, 999).getTime());
    expect(location()).toBe(
      `/processing?taskType=StockSync&status=Failed&channelId=${POS_ID}&correlationId=corr-sync-7` +
        `&errorCode=channel_rejected&from=2026-10-01&to=2026-10-02`,
    );

    await user.click(screen.getByRole('button', { name: 'Xoá bộ lọc' }));
    await waitFor(() => expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20' }));
    expect(location()).toBe('/processing');
    expect(screen.getByRole('textbox', { name: 'Mã tương quan' })).toHaveValue('');
    expect(screen.getByRole('textbox', { name: 'Mã lỗi' })).toHaveValue('');
    expect(startInput()).toHaveValue('');
    expect(filter('Kênh bán').closest('.ant-select')).not.toHaveTextContent('POS');
  });

  it('sorts through the address: created ascending and back to the default, updated newest first', async () => {
    const { requests } = serveList([failedSync, orderTask], 45);
    const { user, location } = renderProcessing('/processing?page=2&status=Failed');
    await screen.findByText('channel_rejected');

    // The default is newest first, so the first click on "created" asks for the oldest first.
    await user.click(screen.getByRole('columnheader', { name: /Ngày tạo/ }));
    await waitFor(() =>
      expect(requests.at(-1)).toEqual({
        page: '1',
        pageSize: '20',
        status: 'Failed',
        sortBy: 'createdAt',
        sortDir: 'asc',
      }),
    );
    expect(location()).toBe('/processing?status=Failed&sortDir=asc');

    await user.click(screen.getByRole('columnheader', { name: /Ngày tạo/ }));
    await waitFor(() => expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', status: 'Failed' }));
    expect(location()).toBe('/processing?status=Failed');
    expect(sortOf(/Ngày tạo/)).toBe('descending');

    await user.click(screen.getByRole('columnheader', { name: /Cập nhật lúc/ }));
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ sortBy: 'updatedAt', sortDir: 'desc' }));
    expect(location()).toBe('/processing?status=Failed&sortBy=updatedAt&sortDir=desc');
    expect(sortOf(/Cập nhật lúc/)).toBe('descending');
    expect(sortOf(/Ngày tạo/)).toBeNull();
  });

  it('ignores address values that are not valid and never sends them', async () => {
    const { requests } = serveList();
    renderProcessing(
      '/processing?taskType=Nope&status=Done&channelId=pos&from=2026-13-45&to=soon&sortBy=secret&page=x',
    );

    await screen.findByText('channel_rejected');
    expect(requests).toEqual([{ page: '1', pageSize: '20' }]);
    expect(startInput()).toHaveValue('');
    expect(sortOf(/Ngày tạo/)).toBe('descending');
  });

  it('shows what the API rejects above the table, not an empty list', async () => {
    const { state } = serveList();
    state.answer = () =>
      problem(400, { errorCode: 'validation_failed', errors: { to: ["'Đến ngày' không được sớm hơn 'Từ ngày'."] } });
    const { user } = renderProcessing('/processing?from=2026-10-02&to=2026-10-01');

    expect(await screen.findByRole('alert')).toHaveTextContent("'Đến ngày' không được sớm hơn 'Từ ngày'.");
    expect(document.querySelectorAll('tbody tr.ant-table-row')).toHaveLength(0);
    expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();

    state.answer = undefined;
    await user.click(screen.getByRole('button', { name: /Làm mới/ }));
    expect(await screen.findByText('channel_rejected')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('opens the detail with the list address, and "Back" returns to the same page, sort and filters', async () => {
    const { requests } = serveList([failedSync, orderTask], 45);
    serveTask(failedSyncDetail());
    const address = `/processing?taskType=StockSync&status=Failed&channelId=${POS_ID}&sortBy=updatedAt&sortDir=asc&page=2`;
    const { user, location, router } = renderProcessing(address);
    await screen.findByText('channel_rejected');

    // The creation time is the link ...
    await user.click(screen.getByRole('link', { name: '02/10/2026 09:30' }));
    expect(await screen.findByTestId('task-summary')).toBeInTheDocument();
    expect(location()).toBe(`/processing/tasks/${SYNC_TASK_ID}`);
    expect(router.state.location.state).toEqual({ from: address });
    // The menu still marks "Sync & Processing".
    expect(screen.getByRole('menuitem', { name: /Đồng bộ & xử lý/ })).toHaveClass('ant-menu-item-selected');

    await user.click(screen.getByRole('button', { name: /Quay lại/ }));
    await screen.findByText('channel_rejected');
    expect(location()).toBe(address);
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ page: '2', sortBy: 'updatedAt', sortDir: 'asc' }));

    // ... and so is the "Detail" action of the row.
    await user.click(within(rowOf('channel_rejected')).getByRole('button', { name: 'Chi tiết' }));
    await waitFor(() => expect(location()).toBe(`/processing/tasks/${SYNC_TASK_ID}`));
    expect(router.state.location.state).toEqual({ from: address });
  });

  it('leads from a task to its order', async () => {
    serveList();
    server.use(http.get(api('/orders/:id'), () => HttpResponse.json(orderDetail())));
    const { user, location } = renderProcessing();
    await screen.findByText('channel_rejected');

    await user.click(screen.getByRole('link', { name: 'Mở đơn' }));

    expect(await screen.findByRole('heading', { name: 'Đơn hàng WEB-20261001-ABC123' })).toBeInTheDocument();
    expect(location()).toBe(`/orders/${ORDER_ID}`);
  });

  it('is refetched once by a burst of realtime hints about tasks, and not by hints about other things', async () => {
    const { state, requests } = serveList([failedSync]);
    const hub = fakeHub({ autoAccept: true });
    renderProcessing('/processing', staffUser, hub.create);
    await screen.findByText('channel_rejected');
    await waitFor(() => expect(screen.getByTestId('connection-indicator')).toHaveTextContent('Trực tiếp'));
    // The page's own request and the one every new connection causes.
    await pause(FLUSH_WINDOW_MS + 300);
    expect(requests).toHaveLength(2);

    // A new failed task appears without a reload.
    state.items = [taskSummary({ taskId: TASK_ID, status: 'Failed', correlationId: 'corr-new' }), failedSync];
    await hub.latest().send([{ type: 'processing.task.changed', taskId: TASK_ID, taskType: 'OrderProcessing' }]);
    await hub.latest().send([{ type: 'processing.task.changed', taskId: SYNC_TASK_ID, taskType: 'StockSync' }]);
    await waitFor(() => expect(document.querySelectorAll('tbody tr.ant-table-row')).toHaveLength(2));
    await pause(FLUSH_WINDOW_MS + 300);
    expect(requests).toHaveLength(3);

    await hub.latest().send([{ type: 'inventory.changed', productIds: ['p1'] }]);
    await pause(FLUSH_WINDOW_MS + 300);
    expect(requests).toHaveLength(3);
  });

  it('is usable by every role', async () => {
    serveList();
    renderProcessing('/processing', managerUser);

    expect(await screen.findByText('channel_rejected')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Đồng bộ & xử lý' })).toBeInTheDocument();
  });
});

describe('processing list: language switch', () => {
  it('changes text only: address, sort, filters, their controls and the page stay', async () => {
    const { requests, channels } = serveList([failedSync, orderTask], 45);
    const address =
      `/processing?taskType=StockSync&status=Failed&channelId=${WEBSITE_ID}&correlationId=corr-sync-7` +
      `&errorCode=channel_rejected&from=2026-10-01&to=2026-10-02&sortBy=updatedAt&sortDir=desc&page=2`;
    const { user, location } = renderProcessing(address);
    await screen.findByText('channel_rejected');
    await waitFor(() => expect(filter('Kênh bán').closest('.ant-select')).toHaveTextContent('Website'));

    await expectLanguageSwitchChangesTextOnly(user, location, {
      vietnamese: ['Đồng bộ & xử lý', 'Loại tác vụ', 'Thất bại', 'Mã lỗi gần nhất', 'Xoá bộ lọc', '20 / trang'],
      english: ['Sync & processing', 'Task type', 'Failed', 'Last error code', 'Clear filters', '20 / page'],
      requestCount: () => requests.length + channels.count,
    });

    expect(columnTexts('Ngày tạo')).toEqual(['02/10/2026 09:30', '01/10/2026 08:05']);
    expect(screen.getByRole('textbox', { name: 'Mã tương quan' })).toHaveValue('corr-sync-7');
    expect(startInput()).toHaveValue('01/10/2026');
    expect(location()).toBe(address);
  });
});
