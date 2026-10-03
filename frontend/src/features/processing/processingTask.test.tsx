import { act, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import { FLUSH_WINDOW_MS } from '@/features/realtime/hintTags';
import type { HubFactory } from '@/features/realtime/hub';
import { fakeHub } from '@/test/hub';
import { expectLanguageSwitchChangesTextOnly, findDialog, settled } from '@/test/interactions';
import { attempt, ORDER_ID, orderDetail, serveChannels, task, TASK_ID } from '@/test/orders';
import { failedSyncDetail, serveTask, SYNC_TASK_ID, taskChannels, UNKNOWN_CHANNEL_ID } from '@/test/processing';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
const TASK_PATH = `/processing/tasks/${SYNC_TASK_ID}`;

function renderTask(route = TASK_PATH, user: AuthUser = staffUser, createHub?: HubFactory) {
  serveChannels(taskChannels);
  return renderApp({ route, session: sessionOf(user), createHub });
}

const summaryOf = () => screen.getByTestId('task-summary');
const retryButton = () => screen.queryByRole('button', { name: /Thử lại/ });

/** Confirms the retry dialog. */
async function confirmRetry(user: ReturnType<typeof renderApp>['user']) {
  await user.click(screen.getByRole('button', { name: /Thử lại/ }));
  const dialog = await findDialog('Thử lại tác vụ này?');
  expect(dialog).toHaveTextContent('Tác vụ sẽ được đưa lại vào hàng đợi và chạy lại từ đầu.');
  await user.click(within(dialog).getByRole('button', { name: 'Thử lại' }));
}

describe('processing task detail', () => {
  it('shows the task, its message and every attempt as the API sends them - never the message error', async () => {
    const { reads } = serveTask(failedSyncDetail());
    renderTask();

    expect(await screen.findByRole('heading', { name: 'Đồng bộ tồn kho' })).toBeInTheDocument();
    expect(reads).toEqual([SYNC_TASK_ID]);

    const summary = summaryOf();
    expect(summary).toHaveTextContent('Loại tác vụĐồng bộ tồn kho');
    expect(within(summary).getByText('Thất bại').closest('.ant-tag')).toHaveClass('ant-tag-error');
    // The channel's name comes from the channel list.
    await waitFor(() => expect(summary).toHaveTextContent('Kênh bánPOS'));
    expect(summary).toHaveTextContent('SKUTEA-01');
    expect(summary).toHaveTextContent('Số lần thử lại1');
    expect(summary).toHaveTextContent('corr-sync-7');
    expect(summary).toHaveTextContent(SYNC_TASK_ID);
    expect(summary).toHaveTextContent('Ngày tạo02/10/2026 09:30');
    // No order behind a stock-sync task: no link.
    expect(within(summary).queryByRole('link')).not.toBeInTheDocument();

    const message = screen.getByTestId('task-message');
    expect(message).toHaveTextContent('Loại sự kiệnstock.sync.requested');
    expect(within(message).getByText('Đã gửi').closest('.ant-tag')).toHaveClass('ant-tag-success');
    expect(message).toHaveTextContent('Số lần gửi lỗi3');
    expect(message).toHaveTextContent('Mã thông điệpmessage-7');

    const attempts = [...screen.getByTestId('task-attempts').querySelectorAll('tbody tr.ant-table-row')];
    expect(attempts.map((row) => row.querySelectorAll('td')[4]?.textContent)).toEqual([
      'channel_unavailable',
      'channel_rejected',
    ]);
    expect(attempts.map((row) => row.querySelectorAll('td')[3]?.textContent)).toEqual([
      'StockSyncWorker@host-1:4242',
      'StockSyncWorker@host-2:777',
    ]);
    // The attempts' messages are sanitised by the API and shown; the message's `lastError` is not, and never is.
    expect(screen.getByText('The channel rejected the stock update.')).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent('INTERNAL broker text');
  });

  it('links to the order of an order task and shows a channel it does not know by its id', async () => {
    serveTask(
      task({
        taskId: SYNC_TASK_ID,
        status: 'Succeeded',
        orderId: ORDER_ID,
        channelId: UNKNOWN_CHANNEL_ID,
        attempts: [attempt()],
        message: null,
      }),
    );
    renderTask();

    expect(await screen.findByRole('heading', { name: 'Xử lý đơn hàng' })).toBeInTheDocument();
    expect(within(summaryOf()).getByRole('link', { name: 'Mở đơn' })).toHaveAttribute('href', `/orders/${ORDER_ID}`);
    await waitFor(() => expect(summaryOf()).toHaveTextContent(`Kênh bán${UNKNOWN_CHANNEL_ID}`));
    expect(screen.getByTestId('task-message')).toHaveTextContent('Tác vụ này không có thông điệp.');
  });

  it('says so when the task has no attempt yet', async () => {
    serveTask(failedSyncDetail({ status: 'Pending', attempts: [] }));
    renderTask();

    expect(await screen.findByText('Tác vụ này chưa có lần chạy nào.')).toBeInTheDocument();
  });

  it('shows the not-found view for an id that is no GUID, without asking the API', async () => {
    const { reads } = serveTask(failedSyncDetail());
    const { user, location } = renderTask('/processing/tasks/not-a-guid');

    expect(await screen.findByText('Không tìm thấy tác vụ')).toBeInTheDocument();
    await pause(100);
    expect(reads).toEqual([]);

    await user.click(screen.getByRole('button', { name: 'Về danh sách' }));
    await waitFor(() => expect(location()).toBe('/processing'));
  });

  it('shows the not-found view when the API does not know the task', async () => {
    const { reads } = serveTask(null);
    renderTask();

    expect(await screen.findByText('Không tìm thấy tác vụ')).toBeInTheDocument();
    expect(reads).toEqual([SYNC_TASK_ID]);
    // A 404 of a page is not an unexpected error.
    expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();
  });

  it('goes back to the plain list when it was not opened from it, and asks again on "Refresh"', async () => {
    const { state, reads } = serveTask(failedSyncDetail());
    server.use(
      http.get(api('/processing/tasks'), () => HttpResponse.json({ items: [], page: 1, pageSize: 20, totalCount: 0 })),
    );
    const { user, location } = renderTask();
    await screen.findByTestId('task-summary');

    state.detail = failedSyncDetail({ status: 'Succeeded' });
    await user.click(screen.getByRole('button', { name: /Làm mới/ }));
    await waitFor(() => expect(within(summaryOf()).getByText('Thành công')).toBeInTheDocument());
    expect(reads).toHaveLength(2);

    await user.click(screen.getByRole('button', { name: /Quay lại/ }));
    await waitFor(() => expect(location()).toBe('/processing'));

    await settled();
  });

  it('is refetched by a realtime hint that names it', async () => {
    const { state, reads } = serveTask(failedSyncDetail({ status: 'InProgress' }));
    const hub = fakeHub({ autoAccept: true });
    renderTask(TASK_PATH, staffUser, hub.create);
    await screen.findByTestId('task-summary');
    await waitFor(() => expect(screen.getByTestId('connection-indicator')).toHaveTextContent('Trực tiếp'));
    await pause(FLUSH_WINDOW_MS + 300);
    const before = reads.length;

    // Another task: nothing is asked.
    await hub.latest().send([{ type: 'processing.task.changed', taskId: TASK_ID, taskType: 'OrderProcessing' }]);
    await pause(FLUSH_WINDOW_MS + 300);
    expect(reads).toHaveLength(before);

    state.detail = failedSyncDetail({ status: 'Succeeded' });
    await hub.latest().send([{ type: 'processing.task.changed', taskId: SYNC_TASK_ID, taskType: 'StockSync' }]);
    await waitFor(() => expect(within(summaryOf()).getByText('Thành công')).toBeInTheDocument());
    expect(reads).toHaveLength(before + 1);
  });
});

describe('manual retry', () => {
  it('is offered to Manager and Admin only, and only for a failed task', async () => {
    const { state } = serveTask(failedSyncDetail());

    const staff = renderTask(TASK_PATH, staffUser);
    await screen.findByTestId('task-summary');
    expect(retryButton()).not.toBeInTheDocument();
    staff.unmount();

    const admin = renderTask(TASK_PATH, adminUser);
    await screen.findByTestId('task-summary');
    expect(retryButton()).toBeInTheDocument();
    expect(retryButton()).toHaveStyle({ width: '136px' });
    admin.unmount();

    for (const status of ['Pending', 'InProgress', 'RetryScheduled', 'Succeeded'] as const) {
      state.detail = failedSyncDetail({ status });
      const manager = renderTask(TASK_PATH, managerUser);
      await screen.findByTestId('task-summary');
      expect(retryButton(), status).not.toBeInTheDocument();
      manager.unmount();
    }
  });

  it('asks for a confirmation; cancelling sends nothing', async () => {
    const { retries } = serveTask(failedSyncDetail());
    const { user } = renderTask(TASK_PATH, managerUser);
    await screen.findByTestId('task-summary');

    await user.click(screen.getByRole('button', { name: /Thử lại/ }));
    const dialog = await findDialog('Thử lại tác vụ này?');
    await user.click(within(dialog).getByRole('button', { name: 'Huỷ' }));

    await pause(200);
    expect(retries).toEqual([]);
    expect(within(summaryOf()).getByText('Thất bại')).toBeInTheDocument();
  });

  it('queues the task again after the confirmation: the task is fetched again and shows "Pending"', async () => {
    const { retries, reads } = serveTask(failedSyncDetail());
    const { user } = renderTask(TASK_PATH, managerUser);
    await screen.findByTestId('task-summary');
    expect(reads).toHaveLength(1);

    await confirmRetry(user);

    expect(await screen.findByText('Đã đưa tác vụ vào hàng đợi để thử lại.')).toBeInTheDocument();
    await waitFor(() => expect(within(summaryOf()).getByText('Đang chờ')).toBeInTheDocument());
    expect(retries).toEqual([SYNC_TASK_ID]);
    expect(reads).toHaveLength(2);
    // Not failed any more: nothing to retry.
    expect(retryButton()).not.toBeInTheDocument();
  });

  it('says in the UI language that the task is not failed any more (409) and shows the task as it is now', async () => {
    const { state, retries } = serveTask(failedSyncDetail());
    const { user } = renderTask(TASK_PATH, managerUser);
    await screen.findByTestId('task-summary');

    // Somebody else retried it in the meantime and it succeeded.
    state.retryAnswer = () => {
      state.detail = failedSyncDetail({ status: 'Succeeded' });
      return problem(409, {
        errorCode: 'task_not_failed',
        detail: 'Task is Succeeded; only a Failed task can be retried.',
      });
    };
    await confirmRetry(user);

    expect(
      await screen.findByText('Tác vụ không còn ở trạng thái thất bại nên không thể thử lại.'),
    ).toBeInTheDocument();
    await waitFor(() => expect(within(summaryOf()).getByText('Thành công')).toBeInTheDocument());
    expect(retries).toEqual([SYNC_TASK_ID]);
    expect(retryButton()).not.toBeInTheDocument();
    // A business conflict is not an unexpected error.
    expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();
  });

  it('says that the task does not exist any more (404)', async () => {
    const { state } = serveTask(failedSyncDetail());
    const { user } = renderTask(TASK_PATH, managerUser);
    await screen.findByTestId('task-summary');

    state.retryAnswer = () => problem(404, { errorCode: 'task_not_found' });
    await confirmRetry(user);

    expect(await screen.findByText('Tác vụ xử lý không tồn tại.')).toBeInTheDocument();

    await settled();
  });

  it('leaves the task as it is when the request fails for an unexpected reason (reported globally)', async () => {
    const { state, reads } = serveTask(failedSyncDetail());
    const { user } = renderTask(TASK_PATH, managerUser);
    await screen.findByTestId('task-summary');

    state.retryAnswer = () => problem(500);
    await confirmRetry(user);

    expect(await screen.findByTestId('correlation-id')).toBeInTheDocument();
    expect(within(summaryOf()).getByText('Thất bại')).toBeInTheDocument();
    expect(retryButton()).toBeInTheDocument();
    expect(reads).toHaveLength(1);
  });

  it('is available on the order detail for a failed task of the order, and refreshes the order', async () => {
    const failedOrder = () =>
      orderDetail({
        tasks: [
          {
            ...orderDetail().tasks[0]!,
            status: 'Failed',
            attempts: [attempt({ status: 'Failed', errorCode: 'timeout', errorMessage: 'The database timed out.' })],
          },
        ],
      });
    const state = { order: failedOrder(), orderReads: 0, retries: [] as string[] };
    server.use(
      http.get(api('/orders/:id'), () => {
        state.orderReads += 1;
        return HttpResponse.json(state.order);
      }),
      http.post(api('/processing/tasks/:id/retry'), ({ params }) => {
        state.retries.push(String(params.id));
        state.order = orderDetail({ tasks: [{ ...failedOrder().tasks[0]!, status: 'Pending' }] });
        return HttpResponse.json({ taskId: params.id, status: 'Pending', messageId: 'message-9' }, { status: 202 });
      }),
    );

    // Staff: the failed task is shown, without the action.
    const staff = renderApp({ route: `/orders/${ORDER_ID}`, session: sessionOf(staffUser) });
    const staffTask = await screen.findByTestId(`order-task-${TASK_ID}`);
    expect(within(staffTask).getAllByText('Thất bại').length).toBeGreaterThan(0);
    expect(retryButton()).not.toBeInTheDocument();
    staff.unmount();

    const { user } = renderApp({ route: `/orders/${ORDER_ID}`, session: sessionOf(managerUser) });
    const shown = await screen.findByTestId(`order-task-${TASK_ID}`);
    const readsBefore = state.orderReads;
    await user.click(within(shown).getByRole('button', { name: /Thử lại/ }));
    const dialog = await findDialog('Thử lại tác vụ này?');
    await user.click(within(dialog).getByRole('button', { name: 'Thử lại' }));

    expect(await screen.findByText('Đã đưa tác vụ vào hàng đợi để thử lại.')).toBeInTheDocument();
    expect(state.retries).toEqual([TASK_ID]);
    // The order lists its tasks, so it is fetched again and the task shows "Pending".
    await waitFor(() =>
      expect(within(screen.getByTestId(`order-task-${TASK_ID}`)).getByText('Đang chờ')).toBeInTheDocument(),
    );
    expect(state.orderReads).toBe(readsBefore + 1);
    expect(retryButton()).not.toBeInTheDocument();
  });
});

describe('processing task detail: another task on the mounted page', () => {
  it('never shows the previous task while the next one loads', async () => {
    const next = task({ taskId: TASK_ID, status: 'Succeeded', orderId: ORDER_ID, correlationId: 'corr-task-1' });
    let release = () => {};
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get(api('/processing/tasks/:id'), async ({ params }) => {
        if (params.id !== TASK_ID) {
          return HttpResponse.json(failedSyncDetail());
        }

        await held;
        return HttpResponse.json(next);
      }),
    );
    const { router } = renderTask(TASK_PATH, managerUser);
    await screen.findByRole('heading', { name: 'Đồng bộ tồn kho' });
    expect(summaryOf()).toHaveTextContent('corr-sync-7');
    expect(retryButton()).toBeInTheDocument();

    // The route parameter changes while the page is mounted (a link from one task to another).
    await act(() => router.navigate(`/processing/tasks/${TASK_ID}`));

    // The answer is still on its way: the page says so, with nothing of the first task - also not its retry.
    expect(await screen.findByRole('heading', { name: 'Tác vụ xử lý' })).toBeInTheDocument();
    expect(screen.queryByTestId('task-summary')).not.toBeInTheDocument();
    expect(screen.queryByText('corr-sync-7')).not.toBeInTheDocument();
    expect(screen.queryByTestId('task-attempts')).not.toBeInTheDocument();
    expect(retryButton()).not.toBeInTheDocument();

    release();
    expect(await screen.findByRole('heading', { name: 'Xử lý đơn hàng' })).toBeInTheDocument();
    expect(summaryOf()).toHaveTextContent('corr-task-1');
    expect(retryButton()).not.toBeInTheDocument();
  });
});

describe('processing task detail: language switch', () => {
  it('changes text only: the task, its message and its attempts stay', async () => {
    const { reads } = serveTask(failedSyncDetail());
    const { user, location } = renderTask(TASK_PATH, managerUser);
    await screen.findByTestId('task-summary');
    await waitFor(() => expect(summaryOf()).toHaveTextContent('Kênh bánPOS'));

    await expectLanguageSwitchChangesTextOnly(user, location, {
      vietnamese: ['Đồng bộ tồn kho', 'Thất bại', 'Mã tương quan', 'Thông điệp', 'Đã gửi', 'Các lần chạy', 'Thử lại'],
      english: ['Stock sync', 'Failed', 'Correlation ID', 'Message', 'Published', 'Attempts', 'Retry'],
      requestCount: () => reads.length,
    });

    expect(location()).toBe(TASK_PATH);
    expect(summaryOf()).toHaveTextContent('corr-sync-7');
    expect(screen.getByText('The channel rejected the stock update.')).toBeInTheDocument();
  });
});
