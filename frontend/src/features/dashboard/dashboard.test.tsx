import { screen, waitFor, within } from '@testing-library/react';
import dayjs from 'dayjs';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import { FLUSH_WINDOW_MS } from '@/features/realtime/hintTags';
import type { HubFactory } from '@/features/realtime/hub';
import { DASHBOARD_PRESETS_WIDTH, DASHBOARD_UPDATED_WIDTH } from '@/shared/components/layout';
import { GENERATED_AT, POS_ID, serveSummary, summary, WEBSITE_ID } from '@/test/dashboard';
import { fakeHub } from '@/test/hub';
import { expectLanguageSwitchChangesTextOnly, rowOf } from '@/test/interactions';
import { renderApp } from '@/test/renderApp';
import { managerUser, problem, sessionOf, staffUser } from '@/test/server';

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

function renderDashboard(route = '/', user: AuthUser = staffUser, createHub?: HubFactory) {
  return renderApp({ route, session: sessionOf(user), createHub });
}

const card = (name: string) => within(screen.getByTestId(`dashboard-${name}`));
const preset = (name: string) => screen.getByRole('radio', { name });
const startInput = () => screen.getByPlaceholderText('Từ ngày');
const endInput = () => screen.getByPlaceholderText('Đến ngày');
/** The href of the link a number is, or null when the number is plain text. */
const hrefIn = (cell: HTMLElement | null | undefined) => cell?.querySelector('a')?.getAttribute('href') ?? null;
const cellOf = (rowText: string, column: number) => rowOf(rowText).querySelectorAll('td')[column];

describe('dashboard', () => {
  it('shows every number of the summary as the API sends it, zero rows included', async () => {
    const { requests } = serveSummary();
    renderDashboard();

    expect(await screen.findByText('Đơn hàng trong khoảng thời gian')).toBeInTheDocument();
    // The default range: the server's last 24 hours - nothing is sent.
    expect(requests).toEqual([{}]);
    expect(screen.getByTestId('dashboard-updated')).toHaveTextContent('Cập nhật lúc 08:05:09');

    // Orders in the range.
    const orders = screen.getByTestId('dashboard-orders');
    expect(orders).toHaveTextContent('Số đơn hàng1.234');
    expect(orders).toHaveTextContent('Doanh số (đã giữ hàng và hoàn tất)98.765.432 ₫');
    const [statusTable, channelTable] = [...orders.querySelectorAll('table')];
    expect([...statusTable!.querySelectorAll('tbody tr.ant-table-row')].map((row) => row.textContent)).toEqual([
      'Chờ xử lý0',
      'Đã giữ hàng1.200',
      'Không đủ hàng30',
      'Đã huỷ0',
      'Hoàn tất4',
    ]);
    expect([...channelTable!.querySelectorAll('tbody tr.ant-table-row')].map((row) => row.textContent)).toEqual([
      'Digital Platform Online000',
      'POS34304',
      'Website1.2001.17026',
    ]);

    // Inventory now.
    const inventory = screen.getByTestId('dashboard-inventory');
    expect(inventory).toHaveTextContent('Sản phẩm57');
    expect(inventory).toHaveTextContent('Sắp hết hàng6');
    expect(inventory).toHaveTextContent('Hết hàng2');

    // Processing per task type: every type, zeros included.
    const processing = screen.getByTestId('dashboard-processing');
    expect([...processing.querySelectorAll('tbody tr.ant-table-row')].map((row) => row.textContent)).toEqual([
      'Xử lý đơn hàng31021.230',
      'Đồng bộ tồn kho00572.468',
      'Hoá đơn00000',
      'E-mail00000',
    ]);

    // Stock sync per channel: the last success relative to when the summary was generated, exact time on hover.
    const stockSync = screen.getByTestId('dashboard-stock-sync');
    expect([...stockSync.querySelectorAll('tbody tr.ant-table-row')].map((row) => row.textContent)).toEqual([
      'Digital Platform Online00Chưa có',
      'POS573 giờ trước',
      'Website002 phút trước',
    ]);

    // Outbox: no number is judged, coloured or compared here.
    const outbox = screen.getByTestId('dashboard-outbox');
    expect(outbox).toHaveTextContent('Đang chờ gửi3');
    expect(outbox).toHaveTextContent('Gửi thất bại1');
    expect(outbox).toHaveTextContent('Chờ lâu nhất5 phút');
  });

  it('shows the exact time of a relative time on hover, and "none" / "never" when there is nothing', async () => {
    serveSummary(
      summary({
        outbox: { pending: 0, failed: 0, oldestPendingAt: null },
        stockSync: [
          {
            channelId: WEBSITE_ID,
            slug: 'website',
            name: 'Website',
            retryScheduled: 0,
            failed: 0,
            lastSucceededAt: new Date(2026, 9, 2, 8, 3, 9).toISOString(),
          },
        ],
      }),
    );
    const { user } = renderDashboard();
    await screen.findByText('Đơn hàng trong khoảng thời gian');

    expect(screen.getByTestId('dashboard-outbox')).toHaveTextContent('Chờ lâu nhấtKhông có');
    await user.hover(card('stock-sync').getByText('2 phút trước'));
    expect(await screen.findByText('02/10/2026 08:03')).toBeInTheDocument();
  });

  it('links a non-zero number to its list with the matching filters, and leaves zeros as text', async () => {
    serveSummary();
    renderDashboard();
    await screen.findByText('Đơn hàng trong khoảng thời gian');

    // Low stock → the inventory list with its low-stock filter.
    expect(card('inventory').getByRole('link', { name: '6' })).toHaveAttribute('href', '/inventory?lowStockOnly=true');

    // Processing: task type + status; finished tasks newest first.
    expect(hrefIn(cellOf('Xử lý đơn hàng', 1))).toBe('/processing?taskType=OrderProcessing&status=Pending');
    expect(hrefIn(cellOf('Xử lý đơn hàng', 2))).toBe('/processing?taskType=OrderProcessing&status=InProgress');
    expect(hrefIn(cellOf('Xử lý đơn hàng', 3))).toBeNull();
    expect(hrefIn(cellOf('Xử lý đơn hàng', 4))).toBe(
      '/processing?taskType=OrderProcessing&status=Failed&sortBy=updatedAt&sortDir=desc',
    );
    expect(hrefIn(cellOf('Đồng bộ tồn kho', 3))).toBe('/processing?taskType=StockSync&status=RetryScheduled');
    expect(hrefIn(cellOf('Đồng bộ tồn kho', 5))).toBe(
      '/processing?taskType=StockSync&status=Succeeded&sortBy=updatedAt&sortDir=desc',
    );
    expect(rowOf('Hoá đơn').querySelectorAll('a')).toHaveLength(0);

    // Stock sync: the channel is part of the filter.
    const pos = [...screen.getByTestId('dashboard-stock-sync').querySelectorAll('tbody tr.ant-table-row')][1]!;
    expect(hrefIn(pos.querySelectorAll('td')[1])).toBe(
      `/processing?taskType=StockSync&status=RetryScheduled&channelId=${POS_ID}`,
    );
    expect(hrefIn(pos.querySelectorAll('td')[2])).toBe(
      `/processing?taskType=StockSync&status=Failed&channelId=${POS_ID}`,
    );
    const website = [...screen.getByTestId('dashboard-stock-sync').querySelectorAll('tbody tr.ant-table-row')][2]!;
    expect(website.querySelectorAll('a')).toHaveLength(0);
  });

  it('opens the low-stock list from its number', async () => {
    serveSummary();
    const { user, location } = renderDashboard();
    await screen.findByText('Đơn hàng trong khoảng thời gian');

    await user.click(card('inventory').getByRole('link', { name: '6' }));

    await waitFor(() => expect(location()).toBe('/inventory?lowStockOnly=true'));
  });

  it('keeps the range in the address: presets send local days, the default sends nothing', async () => {
    const { requests } = serveSummary();
    const { user, location } = renderDashboard();
    await screen.findByText('Đơn hàng trong khoảng thời gian');
    expect(preset('24 giờ qua')).toBeChecked();
    const today = dayjs();
    const startOf = (daysBack: number) => today.subtract(daysBack, 'day').startOf('day').valueOf();
    const sent = () => requests.at(-1)!;

    await user.click(preset('Hôm nay'));
    await waitFor(() => expect(requests).toHaveLength(2));
    expect(location()).toBe('/?range=today');
    expect(sent().from).toMatch(/T00:00:00[+-]\d{2}:\d{2}$/);
    expect(sent().to).toMatch(/T23:59:59\.999[+-]\d{2}:\d{2}$/);
    expect(new Date(sent().from!).getTime()).toBe(startOf(0));
    expect(new Date(sent().to!).getTime()).toBe(today.endOf('day').valueOf());

    await user.click(preset('7 ngày qua'));
    await waitFor(() => expect(requests).toHaveLength(3));
    expect(location()).toBe('/?range=7d');
    expect(new Date(sent().from!).getTime()).toBe(startOf(6));
    expect(new Date(sent().to!).getTime()).toBe(today.endOf('day').valueOf());

    await user.click(preset('30 ngày qua'));
    await waitFor(() => expect(requests).toHaveLength(4));
    expect(location()).toBe('/?range=30d');
    expect(new Date(sent().from!).getTime()).toBe(startOf(29));

    // Back to the default: nothing in the address, nothing in the request. (The first answer is still cached.)
    await user.click(preset('24 giờ qua'));
    await waitFor(() => expect(location()).toBe('/'));
    await waitFor(() => expect(requests).toHaveLength(5));
    expect(sent()).toEqual({});
    expect(preset('24 giờ qua')).toBeChecked();
  });

  it('takes a custom range of local days from the picker and from the address; no preset is marked then', async () => {
    const { requests } = serveSummary();
    const { user, location } = renderDashboard('/?range=7d');
    await screen.findByText('Đơn hàng trong khoảng thời gian');
    expect(preset('7 ngày qua')).toBeChecked();

    await user.click(startInput());
    await user.type(startInput(), '05/09/2026{Enter}');
    await user.type(endInput(), '06/09/2026{Enter}');

    await waitFor(() => expect(location()).toBe('/?from=2026-09-05&to=2026-09-06'));
    await waitFor(() => expect(requests.at(-1)?.to).toMatch(/^2026-09-06T23:59:59\.999/));
    expect(new Date(requests.at(-1)!.from!).getTime()).toBe(new Date(2026, 8, 5).getTime());
    expect(new Date(requests.at(-1)!.to!).getTime()).toBe(new Date(2026, 8, 6, 23, 59, 59, 999).getTime());
    for (const name of ['24 giờ qua', 'Hôm nay', '7 ngày qua', '30 ngày qua']) {
      expect(preset(name)).not.toBeChecked();
    }

    // A preset replaces the custom range.
    await user.click(preset('Hôm nay'));
    await waitFor(() => expect(location()).toBe('/?range=today'));
    expect(startInput()).toHaveValue('');
    expect(endInput()).toHaveValue('');
  });

  it('restores a custom range from the address (reload) and ignores values that are not valid', async () => {
    const { requests } = serveSummary();
    const first = renderDashboard('/?from=2026-09-01&to=2026-09-30&range=today');
    await screen.findByText('Đơn hàng trong khoảng thời gian');

    // Dates win over a preset.
    expect(startInput()).toHaveValue('01/09/2026');
    expect(endInput()).toHaveValue('30/09/2026');
    expect(preset('Hôm nay')).not.toBeChecked();
    expect(new Date(requests[0]!.from!).getTime()).toBe(new Date(2026, 8, 1).getTime());
    expect(new Date(requests[0]!.to!).getTime()).toBe(new Date(2026, 8, 30, 23, 59, 59, 999).getTime());
    first.unmount();

    renderDashboard('/?range=year&from=2026-13-45&to=soon');
    await screen.findByText('Đơn hàng trong khoảng thời gian');
    expect(requests.at(-1)).toEqual({});
    expect(preset('24 giờ qua')).toBeChecked();
    expect(startInput()).toHaveValue('');
  });

  it('shows a range the server rejects on the range control, without numbers, and recovers with another range', async () => {
    const { state, requests } = serveSummary();
    state.answer = () =>
      problem(400, {
        errorCode: 'validation_failed',
        errors: { to: ["Khoảng thời gian từ 'Từ ngày' không được dài quá 92 ngày."] },
      });
    const { user } = renderDashboard('/?from=2026-01-01&to=2026-09-30');

    const alert = await screen.findByTestId('dashboard-range-error');
    expect(alert).toHaveTextContent("Khoảng thời gian từ 'Từ ngày' không được dài quá 92 ngày.");
    expect(startInput().closest('.ant-picker')).toHaveClass('ant-picker-status-error');
    // The limit is the server's: the range was sent as it is.
    expect(requests).toHaveLength(1);
    expect(screen.queryByTestId('dashboard-orders')).not.toBeInTheDocument();
    // Not reported as an unexpected error.
    expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();

    state.answer = undefined;
    await user.click(preset('7 ngày qua'));
    expect(await screen.findByTestId('dashboard-orders')).toBeInTheDocument();
    expect(screen.queryByTestId('dashboard-range-error')).not.toBeInTheDocument();
    expect(startInput().closest('.ant-picker')).not.toHaveClass('ant-picker-status-error');
  });

  it('asks again on "Refresh" and whenever the page is opened, and never polls', async () => {
    const { state, requests } = serveSummary();
    const { user } = renderDashboard('/', managerUser);
    await screen.findByText('Đơn hàng trong khoảng thời gian');

    state.summary = summary({ generatedAt: new Date(GENERATED_AT.getTime() + 61_000).toISOString() });
    await user.click(screen.getByRole('button', { name: /Làm mới/ }));
    await waitFor(() => expect(screen.getByTestId('dashboard-updated')).toHaveTextContent('Cập nhật lúc 08:06:10'));
    expect(requests).toHaveLength(2);

    await pause(2500);
    expect(requests).toHaveLength(2);

    await user.click(screen.getByRole('link', { name: 'Cảnh báo' }));
    await screen.findByText('Trang này sẽ có ở task 018.');
    await user.click(screen.getByRole('link', { name: 'Tổng quan' }));
    await screen.findByText('Đơn hàng trong khoảng thời gian');
    await waitFor(() => expect(requests).toHaveLength(3));
  });

  it('is refetched at once by a realtime hint, and once more by everything else of the same second', async () => {
    const { state, requests } = serveSummary();
    const hub = fakeHub({ autoAccept: true });
    renderDashboard('/', staffUser, hub.create);
    await screen.findByText('Đơn hàng trong khoảng thời gian');
    await waitFor(() => expect(screen.getByTestId('connection-indicator')).toHaveTextContent('Trực tiếp'));
    // The page's own request and, when the page was there before the connection, the one every new connection causes.
    await pause(FLUSH_WINDOW_MS + 300);
    const loaded = requests.length;

    // Idle: the first hint is not held back.
    state.summary = summary({ orders: { ...summary().orders, total: 1235 } });
    await hub.latest().send([{ type: 'order.created', orderId: 'o1' }]);
    await waitFor(() => expect(screen.getByTestId('dashboard-orders')).toHaveTextContent('Số đơn hàng1.235'));
    expect(requests).toHaveLength(loaded + 1);

    // A burst within the second of that refetch: one more request, when the second is over.
    state.summary = summary({ orders: { ...summary().orders, total: 1236 } });
    await hub.latest().send([{ type: 'inventory.changed', productIds: ['p1'] }]);
    await hub.latest().send([{ type: 'processing.task.changed', taskId: 't1', taskType: 'StockSync' }]);
    await hub.latest().send([{ type: 'order.created', orderId: 'o2' }]);
    await pause(200);
    expect(requests).toHaveLength(loaded + 1);

    await waitFor(() => expect(screen.getByTestId('dashboard-orders')).toHaveTextContent('Số đơn hàng1.236'));
    await pause(FLUSH_WINDOW_MS + 300);
    expect(requests).toHaveLength(loaded + 2);
    expect(requests.at(-1)).toEqual({});
  });
});

describe('dashboard: language switch', () => {
  it('changes text only: the range in the address, the tables and the fixed widths stay', async () => {
    const { requests } = serveSummary();
    const { user, location } = renderDashboard('/?range=7d');
    await screen.findByText('Đơn hàng trong khoảng thời gian');
    const widths = () => [
      screen.getByRole('radiogroup', { name: /Khoảng thời gian|Time range/ }).getAttribute('style'),
      screen.getByTestId('dashboard-updated').getAttribute('style'),
    ];
    const before = widths();
    expect(before[0]).toContain(`width: ${DASHBOARD_PRESETS_WIDTH}px`);
    expect(before[1]).toContain(`width: ${DASHBOARD_UPDATED_WIDTH}px`);

    await expectLanguageSwitchChangesTextOnly(user, location, {
      vietnamese: [
        'Tổng quan',
        'Đơn hàng trong khoảng thời gian',
        '7 ngày qua',
        'Tồn kho hiện tại',
        'Đồng bộ tồn kho theo kênh',
        '2 phút trước',
        'Chưa có',
        'Cập nhật lúc 08:05:09',
      ],
      english: [
        'Dashboard',
        'Orders in the range',
        'Last 7 days',
        'Inventory now',
        'Stock sync by channel',
        '2 minutes ago',
        'Never',
        'Updated at 08:05:09',
      ],
      requestCount: () => requests.length,
    });

    expect(location()).toBe('/?range=7d');
    expect(widths()).toEqual(before);
    expect(screen.getByRole('radio', { name: '7 ngày qua' })).toBeChecked();
    expect(
      [...screen.getByTestId('dashboard-processing').querySelectorAll('tbody tr.ant-table-row')].map(
        (row) => row.querySelectorAll('td')[5]?.textContent,
      ),
    ).toEqual(['1.230', '2.468', '0', '0']);
  });
});
