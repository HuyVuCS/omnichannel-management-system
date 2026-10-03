import { act, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import { expectLanguageSwitchChangesTextOnly, settled } from '@/test/interactions';
import { attempt, ORDER_ID, orderDetail, serveChannels, TASK_ID } from '@/test/orders';
import { renderApp } from '@/test/renderApp';
import { api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';
import type { OrderDetail } from './ordersApi';

const emptyPage = { items: [], page: 1, pageSize: 20, totalCount: 0 };
const SECOND_TASK_ID = '0199a1b2-0000-7000-8000-0000000000bb';

/** An order with a task that needed two attempts and a task that has not been run yet. */
const order = orderDetail({
  tasks: [
    {
      ...orderDetail().tasks[0]!,
      retryCount: 1,
      attempts: [
        attempt({
          attemptNo: 1,
          status: 'Failed',
          workerInstanceId: 'order-worker@host-1:4242',
          errorCode: 'db_unavailable',
          errorMessage: 'The database did not answer in time.',
        }),
        attempt({
          attemptNo: 2,
          workerInstanceId: 'order-worker@host-2:5151',
          startedAt: new Date(2026, 9, 1, 8, 5, 30).toISOString(),
          finishedAt: new Date(2026, 9, 1, 8, 5, 31).toISOString(),
        }),
      ],
    },
    {
      taskId: SECOND_TASK_ID,
      taskType: 'StockSync',
      status: 'Pending',
      retryCount: 0,
      correlationId: 'corr-task-2',
      messageId: null,
      createdAt: new Date(2026, 9, 1, 8, 6).toISOString(),
      updatedAt: null,
      attempts: [],
    },
  ],
});

/** `GET /api/orders/{id}`; records the ids. `answer` can replace the response. */
function serveOrder(initial: OrderDetail = order) {
  const state: { order: OrderDetail; answer?: () => Response } = { order: initial };
  const requests: string[] = [];
  server.use(
    http.get(api('/orders/:id'), ({ params }) => {
      requests.push(String(params.id));
      return state.answer ? state.answer() : HttpResponse.json(state.order);
    }),
  );
  return { state, requests };
}

function renderDetail(route = `/orders/${ORDER_ID}`, user: AuthUser = staffUser) {
  return renderApp({ route, session: sessionOf(user) });
}

/** The value next to a label of a summary (`Descriptions`). */
function valueOf(container: HTMLElement, label: string): string {
  const cell = within(container)
    .getAllByText(label)
    .find((element) => element.closest('th.ant-descriptions-item-label'))
    ?.closest('th')?.nextElementSibling;
  if (!cell) {
    throw new Error(`No summary value for "${label}"`);
  }

  return cell.textContent ?? '';
}

const summary = () => document.querySelector<HTMLElement>('main .ant-descriptions')!;
const taskBlock = (taskId: string) => screen.getByTestId(`order-task-${taskId}`);

describe('order detail', () => {
  it('shows the summary, the lines and every task with its attempts as the API sends them', async () => {
    const { requests } = serveOrder();
    renderDetail();

    expect(await screen.findByRole('heading', { name: 'Đơn hàng WEB-20261001-ABC123' })).toBeInTheDocument();
    expect(requests).toEqual([ORDER_ID]);

    expect(valueOf(summary(), 'Mã đơn hàng bên ngoài')).toBe('WEB-20261001-ABC123');
    expect(valueOf(summary(), 'Trạng thái')).toBe('Đã giữ hàng');
    expect(valueOf(summary(), 'Kênh bán')).toBe('Website (website)');
    expect(valueOf(summary(), 'Khách hàng')).toBe('Nguyễn Văn An');
    expect(valueOf(summary(), 'Số dòng')).toBe('2');
    expect(valueOf(summary(), 'Tổng tiền')).toBe('1.234.567 ₫');
    expect(valueOf(summary(), 'Ngày tạo')).toBe('01/10/2026 08:05');
    // The order id is shown in full with a copy button.
    expect(valueOf(summary(), 'Mã đơn hàng (hệ thống)')).toBe(ORDER_ID);
    expect(
      within(summary()).getByText(ORDER_ID).closest('.ant-typography')?.querySelector('.ant-typography-copy'),
    ).not.toBeNull();
    // Labels have a fixed width, so the values do not move with the language.
    expect(summary().querySelector('th')).toHaveStyle({ width: '220px' });

    // Lines: every number is the API's (the line total is not unit price x quantity computed here).
    const lines = screen.getByText('Các dòng hàng').closest<HTMLElement>('.ant-card')!;
    expect(
      within(lines)
        .getAllByRole('row')
        .slice(1)
        .map((row) => row.textContent),
    ).toEqual(['COF-01Cà phê2100.000 ₫234.567 ₫', 'TEA-01Trà xanh10010.000 ₫1.000.000 ₫']);
    expect(lines.querySelector('.ant-pagination')).toBeNull();

    // First task: two attempts, in order, with worker, instance and error.
    const first = taskBlock(TASK_ID);
    expect(valueOf(first, 'Loại tác vụ')).toBe('Xử lý đơn hàng');
    expect(valueOf(first, 'Trạng thái')).toBe('Thành công');
    expect(valueOf(first, 'Số lần thử lại')).toBe('1');
    expect(valueOf(first, 'Mã tương quan')).toBe('corr-task-1');
    expect(
      within(first).getByText('corr-task-1').closest('.ant-typography')?.querySelector('.ant-typography-copy'),
    ).not.toBeNull();
    expect(valueOf(first, 'Mã thông điệp')).toBe('message-1');
    expect(valueOf(first, 'Cập nhật lúc')).toBe('01/10/2026 08:05');
    const attempts = within(first)
      .getAllByRole('row')
      .filter((row) => row.classList.contains('ant-table-row'));
    expect(attempts.map((row) => [...row.querySelectorAll('td')].map((cell) => cell.textContent))).toEqual([
      [
        '1',
        'Thất bại',
        'order-worker',
        'order-worker@host-1:4242',
        'db_unavailable',
        'The database did not answer in time.',
        '01/10/2026 08:05',
        '01/10/2026 08:05',
      ],
      ['2', 'Thành công', 'order-worker', 'order-worker@host-2:5151', '', '', '01/10/2026 08:05', '01/10/2026 08:05'],
    ]);
    expect(within(attempts[0]!).getByText('Thất bại').closest('.ant-tag')).toHaveClass('ant-tag-error');
    expect(within(attempts[1]!).getByText('Thành công').closest('.ant-tag')).toHaveClass('ant-tag-success');
    // A long error message is one truncated line; the full text is its tooltip.
    expect(within(attempts[0]!).getByText('The database did not answer in time.')).toHaveClass(
      'ant-typography-ellipsis',
    );

    // Second task: not run yet - said in words, no empty table.
    const second = taskBlock(SECOND_TASK_ID);
    expect(valueOf(second, 'Loại tác vụ')).toBe('Đồng bộ tồn kho');
    expect(valueOf(second, 'Trạng thái')).toBe('Đang chờ');
    expect(valueOf(second, 'Mã thông điệp')).toBe('');
    expect(within(second).getByText('Tác vụ này chưa có lần chạy nào.')).toBeInTheDocument();
    expect(second.querySelector('table.ant-table, .ant-table')).toBeNull();

    // Manual retry belongs to the processing page (a later task).
    expect(screen.queryByRole('button', { name: /thử lại|retry/i })).not.toBeInTheDocument();
  });

  it('says so when the order has no processing task', async () => {
    serveOrder(orderDetail({ tasks: [] }));
    renderDetail();

    expect(await screen.findByText('Đơn hàng chưa có tác vụ xử lý nào.')).toBeInTheDocument();
  });

  it('shows the not-found view for an id that is no GUID, without asking the API', async () => {
    const { requests } = serveOrder();
    serveChannels();
    server.use(http.get(api('/orders'), () => HttpResponse.json(emptyPage)));
    const { user: appUser, location } = renderDetail('/orders/not-an-id');

    expect(await screen.findByText('Không tìm thấy đơn hàng')).toBeInTheDocument();
    expect(requests).toEqual([]);
    // The menu still shows where the user is.
    expect(screen.getByRole('link', { name: 'Đơn hàng' }).closest('li')).toHaveClass('ant-menu-item-selected');

    await appUser.click(screen.getByRole('button', { name: 'Về danh sách' }));
    await screen.findByRole('heading', { name: 'Đơn hàng' });
    expect(location()).toBe('/orders');

    await settled();
  });

  it('shows the not-found view when the API does not know the order', async () => {
    const { state, requests } = serveOrder();
    state.answer = () => problem(404, { errorCode: 'order_not_found' });
    renderDetail();

    expect(await screen.findByText('Không tìm thấy đơn hàng')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Về danh sách' })).toBeInTheDocument();
    expect(requests).toEqual([ORDER_ID]);
    // Not reported as an unexpected error.
    expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();
  });

  it('shows a rejection the user can act on above the page, and the order after "Refresh"', async () => {
    const { state, requests } = serveOrder();
    state.answer = () => problem(403);
    const { user: appUser } = renderDetail();

    expect(await screen.findByRole('alert')).toHaveTextContent('Bạn không có quyền thực hiện thao tác này.');

    state.answer = undefined;
    await appUser.click(screen.getByRole('button', { name: /Làm mới/ }));
    expect(await screen.findByRole('heading', { name: 'Đơn hàng WEB-20261001-ABC123' })).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(requests).toHaveLength(2);
  });

  it('leads to the movements of this order, and "Back" goes to the list when the detail was not opened from it', async () => {
    serveOrder();
    serveChannels();
    const movementRequests: Record<string, string>[] = [];
    server.use(
      http.get(api('/inventory/movements'), ({ request }) => {
        movementRequests.push(Object.fromEntries(new URL(request.url).searchParams));
        return HttpResponse.json(emptyPage);
      }),
      http.get(api('/orders'), () => HttpResponse.json(emptyPage)),
    );
    const { user: appUser, location, router } = renderDetail(`/orders/${ORDER_ID}`, managerUser);
    await screen.findByRole('heading', { name: 'Đơn hàng WEB-20261001-ABC123' });

    // Fixed widths for the buttons whose text differs per language.
    expect(screen.getByRole('button', { name: /Quay lại/ })).toHaveStyle({ width: '136px' });
    const movements = screen.getByRole('button', { name: /Biến động của đơn này/ });
    expect(movements).toHaveStyle({ width: '280px' });

    await appUser.click(movements);
    expect(await screen.findByRole('heading', { name: 'Biến động tồn kho' })).toBeInTheDocument();
    expect(location()).toBe(`/inventory/movements?referenceType=Order&referenceId=${ORDER_ID}`);
    await waitFor(() =>
      expect(movementRequests.at(-1)).toEqual({
        page: '1',
        pageSize: '20',
        referenceType: 'Order',
        referenceId: ORDER_ID,
      }),
    );

    await router.navigate(`/orders/${ORDER_ID}`);
    await screen.findByRole('heading', { name: 'Đơn hàng WEB-20261001-ABC123' });
    await appUser.click(screen.getByRole('button', { name: /Quay lại/ }));
    await screen.findByRole('heading', { name: 'Đơn hàng' });
    expect(location()).toBe('/orders');

    await settled();
  });

  it('asks the API again whenever the page is opened', async () => {
    const { requests } = serveOrder();
    server.use(http.get(api('/customers'), () => HttpResponse.json(emptyPage)));
    const { user: appUser, router } = renderDetail();
    await screen.findByRole('heading', { name: 'Đơn hàng WEB-20261001-ABC123' });

    await appUser.click(screen.getByRole('link', { name: 'Khách hàng' }));
    await screen.findByRole('heading', { name: 'Khách hàng' });
    await router.navigate(`/orders/${ORDER_ID}`);
    await screen.findByRole('heading', { name: 'Đơn hàng WEB-20261001-ABC123' });

    // Tasks and attempts change in the background: the cached order is not enough.
    await waitFor(() => expect(requests).toHaveLength(2));

    await settled();
  });
});

describe('order detail: another order on the mounted page', () => {
  it('never shows the previous order while the next one loads', async () => {
    const NEXT_ORDER_ID = '0199a1b2-0000-7000-8000-000000000002';
    const next = orderDetail({ orderId: NEXT_ORDER_ID, externalOrderId: 'POS-20261002-ZZ0002', totalAmount: 45000 });
    let release = () => {};
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get(api('/orders/:id'), async ({ params }) => {
        if (params.id !== NEXT_ORDER_ID) {
          return HttpResponse.json(order);
        }

        await held;
        return HttpResponse.json(next);
      }),
    );
    const { router } = renderDetail();
    await screen.findByRole('heading', { name: 'Đơn hàng WEB-20261001-ABC123' });
    expect(valueOf(summary(), 'Tổng tiền')).toBe('1.234.567 ₫');

    // The route parameter changes while the page is mounted (a link from one order to another).
    await act(() => router.navigate(`/orders/${NEXT_ORDER_ID}`));

    // The answer is still on its way: the page says so, with nothing of the first order.
    expect(await screen.findByRole('heading', { name: 'Đơn hàng' })).toBeInTheDocument();
    expect(screen.queryByText('WEB-20261001-ABC123')).not.toBeInTheDocument();
    expect(document.querySelector('main .ant-descriptions')).toBeNull();
    expect(screen.queryByTestId(`order-task-${TASK_ID}`)).not.toBeInTheDocument();

    release();
    expect(await screen.findByRole('heading', { name: 'Đơn hàng POS-20261002-ZZ0002' })).toBeInTheDocument();
    expect(valueOf(summary(), 'Tổng tiền')).toBe('45.000 ₫');
  });
});

describe('order detail: language switch', () => {
  it('changes text only: the order, its lines and its tasks stay', async () => {
    const { requests } = serveOrder();
    const { user: appUser, location } = renderDetail();
    await screen.findByRole('heading', { name: 'Đơn hàng WEB-20261001-ABC123' });
    const blocks = [taskBlock(TASK_ID), taskBlock(SECOND_TASK_ID)];

    await expectLanguageSwitchChangesTextOnly(appUser, location, {
      vietnamese: [
        'Đơn hàng WEB-20261001-ABC123',
        'Các dòng hàng',
        'Đơn giá',
        'Loại tác vụ',
        'Phiên bản worker',
        'Quay lại',
      ],
      english: ['Order WEB-20261001-ABC123', 'Lines', 'Unit price', 'Task type', 'Worker instance', 'Back'],
      requestCount: () => requests.length,
    });

    expect([taskBlock(TASK_ID), taskBlock(SECOND_TASK_ID)]).toEqual(blocks);
    expect(valueOf(summary(), 'Tổng tiền')).toBe('1.234.567 ₫');
    expect(valueOf(summary(), 'Ngày tạo')).toBe('01/10/2026 08:05');
    const lines = screen.getByText('Các dòng hàng').closest<HTMLElement>('.ant-card')!;
    expect(lines).toHaveTextContent('234.567 ₫');
    expect(lines).toHaveTextContent('1.000.000 ₫');
    expect(location()).toBe(`/orders/${ORDER_ID}`);
  });
});
