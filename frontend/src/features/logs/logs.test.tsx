import { act, screen, waitFor, within } from '@testing-library/react';
import dayjs from 'dayjs';
import { http } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import { openOptions } from '@/test/catalog';
import {
  chooseOption,
  columnTexts,
  expectLanguageSwitchChangesTextOnly,
  switchLanguage,
  type AppUser,
  settled,
} from '@/test/interactions';
import { ORDER_ID, TASK_ID } from '@/test/orders';
import { SYNC_TASK_ID } from '@/test/processing';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';
import {
  acknowledgedEvent,
  everyEventType,
  importedEvent,
  insufficientEvent,
  managerEvents,
  orderRejectedEvent,
  processingFailedEvent,
  reconciliationEvent,
  reconciliationWithTroubleEvent,
  serveSystemEvents,
  staleTasksFailedEvent,
  syncRejectedEvent,
  systemEvent,
  userLockedEvent,
} from '@/test/systemEvents';
import type { SystemEvent } from './systemEventsApi';

const PAID_ORDER_ID = '0199a1b2-0000-7000-8000-000000000001';

/** A fixed start of "the last 24 hours", as the page writes it to the address. */
const SINCE = '2026-10-02T01:05:09Z';
const WINDOW = `since=${encodeURIComponent(SINCE)}`;
const BASE = `/logs?${WINDOW}`;
/** What the list asks for with nothing but that window. */
const BASE_PARAMS = { page: '1', pageSize: '20', from: SINCE };

const SINCE_IN_ADDRESS = /^\/logs\?since=(\d{4}-\d{2}-\d{2}T\d{2}%3A\d{2}%3A\d{2}Z)$/;

function renderLogs(route = BASE, user: AuthUser = managerUser) {
  return renderApp({ route, session: sessionOf(user) });
}

const IMPORT_SUMMARY = 'Chứng từ PN/2026-0042 · 2 dòng · 1.500 đơn vị';
const loaded = () => screen.findByText(IMPORT_SUMMARY);
const rows = () => document.querySelectorAll('tbody tr.ant-table-row');
const filter = (name: string) => screen.getByRole('combobox', { name });
const filterText = (name: string) => filter(name).closest('.ant-select')?.textContent ?? '';
const textFilter = (name: string) => screen.getByRole('textbox', { name });
const preset = (name: string) => screen.getByRole('radio', { name });
const startInput = () => screen.getByPlaceholderText('Từ ngày');
const endInput = () => screen.getByPlaceholderText('Đến ngày');
const sortOf = (name: RegExp) => screen.getByRole('columnheader', { name }).getAttribute('aria-sort');
const logsLink = () => screen.queryByRole('link', { name: 'Nhật ký / kiểm toán' });

/** The row of an event (rows are keyed by the log id). */
function rowOfEvent(event: SystemEvent): HTMLElement {
  const row = document.querySelector<HTMLElement>(`tr[data-row-key="${event.logId}"]`);
  if (!row) {
    throw new Error(`No row for event ${event.logId}`);
  }

  return row;
}

/** The cell of an event's row in the column with this header. */
function cellOf(event: SystemEvent, headerText: string): HTMLElement {
  const index = [...document.querySelectorAll('thead th')].findIndex((th) => th.textContent === headerText);
  const cell = rowOfEvent(event).querySelectorAll<HTMLElement>('td')[index];
  if (index < 0 || !cell) {
    throw new Error(`No column "${headerText}"`);
  }

  return cell;
}

/** The labels the open multi-value filter offers. */
async function offeredOptions(user: AppUser, name: string): Promise<string[]> {
  await user.click(filter(name));
  return waitFor(() => {
    const labels = openOptions().map((option) => option.textContent ?? '');
    expect(labels.length).toBeGreaterThan(0);
    return labels;
  });
}

describe('logs: who may read them', () => {
  it('is not for Staff: no menu item, and the address shows the no-access page without asking the API', async () => {
    const { requests } = serveSystemEvents(managerEvents);
    renderLogs('/logs', staffUser);

    expect(await screen.findByText('Không có quyền truy cập')).toBeInTheDocument();
    expect(logsLink()).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Cảnh báo' })).toBeInTheDocument();
    expect(requests).toHaveLength(0);

    await settled();
  });

  it.each([
    ['Manager', managerUser],
    ['Admin', adminUser],
  ] as const)('%s has the menu item and the page', async (_role, user) => {
    serveSystemEvents(managerEvents);
    renderLogs(BASE, user);
    await loaded();

    expect(logsLink()).toHaveAttribute('href', '/logs');
    expect(screen.getByRole('menuitem', { name: /Nhật ký \/ kiểm toán/ })).toHaveClass('ant-menu-item-selected');
    expect(screen.getByRole('heading', { name: 'Nhật ký / kiểm toán' })).toBeInTheDocument();
  });

  it('offers a Manager the event types without user management', async () => {
    serveSystemEvents(managerEvents);
    const { user } = renderLogs();
    await loaded();

    expect(await offeredOptions(user, 'Loại sự kiện')).toEqual([
      'Nhập kho',
      'Đơn thiếu hàng',
      'Đơn đã tồn tại',
      'Thông điệp đơn bị từ chối',
      'Thông điệp đồng bộ bị từ chối',
      'Tác vụ thất bại',
      'Chạy lại thủ công',
      'Tác vụ treo bị đánh dấu thất bại',
      'Gửi thông điệp thất bại',
      'Ghi nhận cảnh báo',
      'Yêu cầu xuất tệp',
      'Thông điệp xuất tệp bị từ chối',
      'Đối soát tồn kho',
      'Thanh toán đơn hàng',
      'Thông điệp hoá đơn bị từ chối',
      'Thông điệp e-mail bị từ chối',
    ]);
  });

  it('offers an Admin the user management events as well', async () => {
    serveSystemEvents(everyEventType);
    const { user } = renderLogs(BASE, adminUser);
    await loaded();

    const options = await offeredOptions(user, 'Loại sự kiện');
    expect(options).toHaveLength(21);
    expect(options.slice(16)).toEqual([
      'Tạo người dùng',
      'Đổi vai trò',
      'Khoá người dùng',
      'Mở khoá người dùng',
      'Đặt lại mật khẩu',
    ]);
  });

  it('never sends a user management type a Manager put into the address; an Admin sends it', async () => {
    const forManager = serveSystemEvents(managerEvents);
    const first = renderLogs(`${BASE}&eventType=user.locked&eventType=processing.failed`);
    await loaded();
    expect(forManager.requests).toEqual([{ params: BASE_PARAMS, level: [], eventType: ['processing.failed'] }]);
    first.unmount();

    const forAdmin = serveSystemEvents(everyEventType);
    renderLogs(`${BASE}&eventType=user.locked&eventType=processing.failed`, adminUser);
    await loaded();
    expect(forAdmin.requests).toEqual([
      { params: BASE_PARAMS, level: [], eventType: ['processing.failed', 'user.locked'] },
    ]);
  });
});

describe('logs: time window', () => {
  it('writes the default - the last 24 hours from now - to the address and asks for it', async () => {
    const { requests } = serveSystemEvents(managerEvents);
    const before = Date.now();
    const { location } = renderLogs('/logs');
    await loaded();

    const since = decodeURIComponent(SINCE_IN_ADDRESS.exec(location())?.[1] ?? '');
    const start = new Date(since).getTime();
    const dayAgo = 24 * 60 * 60 * 1000;
    expect(start).toBeGreaterThanOrEqual(before - dayAgo - 1000);
    expect(start).toBeLessThanOrEqual(Date.now() - dayAgo);
    // Asked once, for that window; no end: events that arrive later are part of it.
    expect(requests).toEqual([{ params: { page: '1', pageSize: '20', from: since }, level: [], eventType: [] }]);
    expect(preset('24 giờ qua')).toBeChecked();
    // The API's default order (newest first) is not asked for, and the table shows it.
    expect(sortOf(/Thời điểm/)).toBe('descending');
  });

  it('asks for the window of the address again after a reload', async () => {
    const { requests } = serveSystemEvents(managerEvents);
    const { location } = renderLogs();
    await loaded();

    expect(requests).toEqual([{ params: BASE_PARAMS, level: [], eventType: [] }]);
    expect(location()).toBe(BASE);
    expect(preset('24 giờ qua')).toBeChecked();
  });

  it('replaces a window in the address that is no instant', async () => {
    const { requests } = serveSystemEvents(managerEvents);
    const { location } = renderLogs('/logs?since=yesterday');
    await loaded();

    expect(location()).toMatch(SINCE_IN_ADDRESS);
    expect(requests).toHaveLength(1);
    expect(requests[0]!.params.from).not.toBe('yesterday');
  });

  it('sends the other presets as local days ending today', async () => {
    const { requests } = serveSystemEvents(managerEvents, 45);
    const { user, location } = renderLogs(`${BASE}&page=2`);
    await loaded();
    const today = dayjs();
    const sent = () => requests.at(-1)!.params;

    await user.click(preset('Hôm nay'));
    await waitFor(() => expect(requests).toHaveLength(2));
    expect(location()).toBe('/logs?range=today');
    expect(sent().page).toBe('1');
    expect(sent().from).toMatch(/T00:00:00[+-]\d{2}:\d{2}$/);
    expect(new Date(sent().from!).getTime()).toBe(today.startOf('day').valueOf());
    expect(new Date(sent().to!).getTime()).toBe(today.endOf('day').valueOf());

    await user.click(preset('7 ngày qua'));
    await waitFor(() => expect(requests).toHaveLength(3));
    expect(location()).toBe('/logs?range=7d');
    expect(new Date(sent().from!).getTime()).toBe(today.subtract(6, 'day').startOf('day').valueOf());

    await user.click(preset('30 ngày qua'));
    await waitFor(() => expect(requests).toHaveLength(4));
    expect(location()).toBe('/logs?range=30d');
    expect(new Date(sent().from!).getTime()).toBe(today.subtract(29, 'day').startOf('day').valueOf());
    expect(new Date(sent().to!).getTime()).toBe(today.endOf('day').valueOf());
    expect(preset('30 ngày qua')).toBeChecked();

    await settled();
  });

  it('counts the last 24 hours from now when that preset is chosen again', async () => {
    const { requests } = serveSystemEvents(managerEvents);
    const { user, location } = renderLogs('/logs?range=7d');
    await loaded();

    await user.click(preset('24 giờ qua'));
    await waitFor(() => expect(location()).toMatch(SINCE_IN_ADDRESS));
    await waitFor(() => expect(requests).toHaveLength(2));
    const since = decodeURIComponent(SINCE_IN_ADDRESS.exec(location())![1]!);
    expect(requests[1]!.params).toEqual({ page: '1', pageSize: '20', from: since });
    expect(Date.now() - new Date(since).getTime()).toBeLessThan(24 * 60 * 60 * 1000 + 5000);
    expect(preset('24 giờ qua')).toBeChecked();

    await settled();
  });

  it('takes a custom range of local days from the picker; no preset is marked then', async () => {
    const { requests } = serveSystemEvents(managerEvents);
    const { user, location } = renderLogs();
    await loaded();

    await user.click(startInput());
    await user.type(startInput(), '01/10/2026{Enter}');
    await user.type(endInput(), '02/10/2026{Enter}');

    await waitFor(() => expect(location()).toBe('/logs?from=2026-10-01&to=2026-10-02'));
    await waitFor(() => expect(requests.at(-1)?.params.to).toMatch(/^2026-10-02T23:59:59\.999/));
    expect(new Date(requests.at(-1)!.params.from!).getTime()).toBe(new Date(2026, 9, 1).getTime());
    expect(new Date(requests.at(-1)!.params.to!).getTime()).toBe(new Date(2026, 9, 2, 23, 59, 59, 999).getTime());
    for (const name of ['24 giờ qua', 'Hôm nay', '7 ngày qua', '30 ngày qua']) {
      expect(preset(name)).not.toBeChecked();
    }

    await settled();
  });

  it("shows the server's rejection of the range on the range control, without rows, and recovers", async () => {
    const { state, requests } = serveSystemEvents(managerEvents);
    const tooLong = "Khoảng thời gian từ 'Từ ngày' đến 'Đến ngày' không được dài quá 92 ngày.";
    state.answer = () => problem(400, { errorCode: 'validation_failed', errors: { to: [tooLong] } });
    const { user } = renderLogs('/logs?from=2026-01-01&to=2026-09-30');

    expect(await screen.findByTestId('logs-range-error')).toHaveTextContent(tooLong);
    expect(startInput().closest('.ant-picker')).toHaveClass('ant-picker-status-error');
    // The limit is the server's: the range was sent as it is, once.
    expect(requests).toHaveLength(1);
    expect(rows()).toHaveLength(0);
    // Said once, on the control - not again above the table, and not as an unexpected error.
    expect(screen.getAllByText(tooLong)).toHaveLength(1);
    expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();

    state.answer = undefined;
    await user.click(screen.getByRole('button', { name: /Làm mới/ }));
    await loaded();
    expect(screen.queryByTestId('logs-range-error')).not.toBeInTheDocument();
  });
});

describe('logs: rows', () => {
  it('says per event type what happened, in Vietnamese', async () => {
    serveSystemEvents(everyEventType);
    renderLogs(BASE, adminUser);
    await loaded();

    expect(columnTexts('Sự kiện')).toEqual([
      'Nhập kho',
      'Đơn thiếu hàng',
      'Đơn đã tồn tại',
      'Thông điệp đơn bị từ chối',
      'Thông điệp đồng bộ bị từ chối',
      'Tác vụ thất bại',
      'Chạy lại thủ công',
      'Gửi thông điệp thất bại',
      'Ghi nhận cảnh báo',
      'Tạo người dùng',
      'Đổi vai trò',
      'Khoá người dùng',
      'Mở khoá người dùng',
      'Đặt lại mật khẩu',
    ]);
    expect(columnTexts('Nội dung')).toEqual([
      IMPORT_SUMMARY,
      'Yêu cầu 100, khả dụng 4',
      'Đơn WEB-20261003-AB12CD đã tồn tại',
      'Thông điệp msg-17 bị từ chối: không phải JSON hợp lệ',
      'Thông điệp msg-18 bị từ chối ở stock.sync.pos: tác vụ thuộc kênh khác',
      'Đồng bộ tồn kho · lần 3 · channel_rejected',
      'Đồng bộ tồn kho: yêu cầu chạy lại',
      'order.created không gửi được sau 10 lần',
      'Cảnh báo: Sắp hết hàng',
      'Vai trò Quản lý',
      'Vai trò: Nhân viên → Quản lý',
      'Tài khoản bị khoá',
      'Tài khoản được mở khoá',
      'Đã đặt mật khẩu tạm thời',
    ]);
  });

  it('says the same in English after a language switch', async () => {
    serveSystemEvents(everyEventType);
    const { user } = renderLogs(BASE, adminUser);
    await loaded();

    await switchLanguage(user, 'EN');
    await screen.findByText('Document PN/2026-0042 · 2 lines · 1.500 units');

    expect(columnTexts('Event')).toEqual([
      'Stock import',
      'Order short of stock',
      'Order already exists',
      'Order message rejected',
      'Sync message rejected',
      'Task failed',
      'Manual retry',
      'Message not published',
      'Alert acknowledged',
      'User created',
      'Role changed',
      'User locked',
      'User unlocked',
      'Password reset',
    ]);
    expect(columnTexts('Summary')).toEqual([
      'Document PN/2026-0042 · 2 lines · 1.500 units',
      'Requested 100, available 4',
      'Order WEB-20261003-AB12CD already exists',
      'Message msg-17 rejected: not valid JSON',
      'Message msg-18 rejected on stock.sync.pos: task belongs to another channel',
      'Stock sync · attempt 3 · channel_rejected',
      'Stock sync: retry requested',
      'order.created not published after 10 attempts',
      'Alert: Low stock',
      'Role Manager',
      'Role: Staff → Manager',
      'Account locked',
      'Account unlocked',
      'Temporary password set',
    ]);
    expect(columnTexts('Level').slice(0, 4)).toEqual(['Info', 'Warning', 'Warning', 'Error']);
  });

  it('shows the time, the level as a tag with icon and text, and who did it to whom', async () => {
    serveSystemEvents(everyEventType);
    renderLogs(BASE, adminUser);
    await loaded();

    expect(columnTexts('Thời điểm').slice(0, 3)).toEqual(['03/10/2026 09:30', '03/10/2026 09:20', '03/10/2026 09:10']);
    expect(columnTexts('Mức độ').slice(0, 6)).toEqual(['Thông tin', 'Cảnh báo', 'Cảnh báo', 'Lỗi', 'Lỗi', 'Lỗi']);
    // Never colour alone.
    for (const event of [importedEvent, insufficientEvent, processingFailedEvent]) {
      const tag = cellOf(event, 'Mức độ').querySelector('.ant-tag');
      expect(tag?.querySelector('[role="img"]'), event.eventType).toBeInTheDocument();
      expect(tag?.textContent, event.eventType).not.toBe('');
    }

    // The actor's e-mail; for user management "actor → target"; a dash when the event names nobody.
    expect(columnTexts('Người thực hiện')).toEqual([
      '—',
      '—',
      '—',
      '—',
      '—',
      '—',
      'manager@example.com',
      '—',
      'manager@example.com',
      ...Array.from({ length: 5 }, () => 'admin@example.com → new.user@example.com'),
    ]);
  });

  it('links an event to its order, its task and its trace', async () => {
    serveSystemEvents(everyEventType);
    renderLogs(BASE, adminUser);
    await loaded();

    // An order event: all three.
    const order = within(cellOf(insufficientEvent, 'Liên kết'));
    expect(order.getByRole('link', { name: 'Đơn hàng' })).toHaveAttribute('href', `/orders/${ORDER_ID}`);
    expect(order.getByRole('link', { name: 'Tác vụ' })).toHaveAttribute('href', `/processing/tasks/${TASK_ID}`);
    expect(order.getByRole('link', { name: 'Luồng' })).toHaveAttribute(
      'href',
      `/logs?${WINDOW}&correlationId=corr-order-7`,
    );

    // A failed stock sync: its task and its trace, no order.
    const failure = within(cellOf(processingFailedEvent, 'Liên kết'));
    expect(failure.getByRole('link', { name: 'Tác vụ' })).toHaveAttribute('href', `/processing/tasks/${SYNC_TASK_ID}`);
    expect(failure.queryByRole('link', { name: 'Đơn hàng' })).not.toBeInTheDocument();
    expect(failure.getByRole('link', { name: 'Luồng' })).toBeInTheDocument();

    // An acknowledged alert: the trace only - nothing leads to an alert.
    expect(within(cellOf(acknowledgedEvent, 'Liên kết')).getAllByRole('link')).toHaveLength(1);
    // An event without a correlation id, an order or a task: a dash.
    expect(cellOf(syncRejectedEvent, 'Liên kết')).toHaveTextContent('—');
    expect(cellOf(syncRejectedEvent, 'Liên kết').querySelectorAll('a')).toHaveLength(0);
  });

  it('keeps the error message of a failure in the tooltip of its summary', async () => {
    serveSystemEvents(managerEvents);
    const { user } = renderLogs();
    await loaded();

    await user.hover(screen.getByText('Đồng bộ tồn kho · lần 3 · channel_rejected'));

    expect(
      await screen.findByText('Đồng bộ tồn kho · lần 3 · channel_rejected — The channel rejected the product.'),
    ).toBeInTheDocument();
  });

  it('shows an unknown event type as it is, without a summary, and a dash where details are missing', async () => {
    const unknown = systemEvent({ logId: 1, eventType: 'order.shipped', details: null, correlationId: 'corr-x' });
    // The API would send details for a type it knows; a client that knows fewer types must not make them up.
    const unknownWithDetails = systemEvent({ logId: 2, eventType: 'order.packed', details: { boxes: 2 } });
    const noDetails = systemEvent({ logId: 3, eventType: 'inventory.imported', details: null });
    const emptyDetails = systemEvent({ logId: 4, eventType: 'processing.failed', level: 'Error', details: {} });
    const noLines = systemEvent({ logId: 5, eventType: 'order.insufficient_stock', details: { items: [] } });
    const twoLines = systemEvent({
      logId: 6,
      eventType: 'order.insufficient_stock',
      details: {
        items: [
          { requested: 5, available: 1 },
          { requested: 2, available: 0 },
        ],
      },
    });
    const newRole = systemEvent({ logId: 7, eventType: 'user.created', details: { role: 'Auditor' } });
    serveSystemEvents([unknown, unknownWithDetails, noDetails, emptyDetails, noLines, twoLines, newRole]);
    renderLogs(BASE, adminUser);
    await screen.findByText('order.shipped');

    expect(columnTexts('Sự kiện')).toEqual([
      'order.shipped',
      'order.packed',
      'Nhập kho',
      'Tác vụ thất bại',
      'Đơn thiếu hàng',
      'Đơn thiếu hàng',
      'Tạo người dùng',
    ]);
    expect(columnTexts('Nội dung')).toEqual([
      '—',
      '—',
      '—',
      // A field the details do not have is a dash inside the line.
      '— · lần — · —',
      '—',
      '2 sản phẩm không đủ hàng',
      // A value this client has no label for is shown as it is.
      'Vai trò Auditor',
    ]);
    expect(columnTexts('Người thực hiện')).toEqual(Array.from({ length: 7 }, () => '—'));
  });

  it('says why a message was rejected: the label of its reason code, an unknown code as it is, a dash without one', async () => {
    const rejected = (logId: number, eventType: string, details: Record<string, unknown>) =>
      systemEvent({ logId, level: 'Error', eventType, details });
    serveSystemEvents([
      rejected(6, 'order.message_rejected', { messageId: 'm-6', reasonCode: 'unknown_task' }),
      // A code of a newer API.
      rejected(5, 'order.message_rejected', { messageId: 'm-5', reasonCode: 'signature_invalid' }),
      // Written before the code existed.
      rejected(4, 'order.message_rejected', { messageId: 'm-4' }),
      rejected(3, 'stock_sync.message_rejected', {
        messageId: 'm-3',
        reasonCode: 'task_mismatch',
        queue: 'stock.sync.pos',
      }),
      rejected(2, 'stock_sync.message_rejected', {
        messageId: 'm-2',
        reasonCode: 'quota_exceeded',
        queue: 'stock.sync.pos',
      }),
      rejected(1, 'stock_sync.message_rejected', { messageId: 'm-1', queue: 'stock.sync.pos' }),
    ]);
    const { user } = renderLogs(BASE, adminUser);
    await screen.findByText('Thông điệp m-6 bị từ chối: tác vụ không tồn tại');

    expect(columnTexts('Nội dung')).toEqual([
      'Thông điệp m-6 bị từ chối: tác vụ không tồn tại',
      'Thông điệp m-5 bị từ chối: signature_invalid',
      'Thông điệp m-4 bị từ chối: —',
      'Thông điệp m-3 bị từ chối ở stock.sync.pos: tác vụ không khớp với thông điệp',
      'Thông điệp m-2 bị từ chối ở stock.sync.pos: quota_exceeded',
      'Thông điệp m-1 bị từ chối ở stock.sync.pos: —',
    ]);

    await switchLanguage(user, 'EN');
    await screen.findByText('Message m-6 rejected: task does not exist');
    expect(columnTexts('Summary')).toEqual([
      'Message m-6 rejected: task does not exist',
      'Message m-5 rejected: signature_invalid',
      'Message m-4 rejected: —',
      'Message m-3 rejected on stock.sync.pos: task does not match the message',
      'Message m-2 rejected on stock.sync.pos: quota_exceeded',
      'Message m-1 rejected on stock.sync.pos: —',
    ]);
  });

  it('says how an order was paid and by whom, and why an invoice or e-mail message was rejected, in both languages', async () => {
    const paid = systemEvent({
      logId: 143,
      eventType: 'order.paid',
      orderId: PAID_ORDER_ID,
      details: { orderId: PAID_ORDER_ID, paymentMethod: 'BankTransfer', amount: 1234567, paidBy: 'user-manager' },
      actorEmail: 'manager@example.com',
    });
    // A method of a newer API, and an amount that is none.
    const paidOtherwise = systemEvent({
      logId: 142,
      eventType: 'order.paid',
      details: { paymentMethod: 'Voucher', amount: '5' },
    });
    const invoiceRejected = systemEvent({
      logId: 141,
      level: 'Error',
      eventType: 'invoice.message_rejected',
      workerName: 'InvoiceWorker',
      details: { messageId: 'msg-51', reasonCode: 'task_mismatch' },
    });
    const emailRejected = systemEvent({
      logId: 140,
      level: 'Error',
      eventType: 'email.message_rejected',
      workerName: 'NotificationWorker',
      details: { messageId: 'msg-52', reasonCode: 'unknown_task' },
    });
    serveSystemEvents([paid, paidOtherwise, invoiceRejected, emailRejected]);
    const { user } = renderLogs();
    await screen.findByText('Chuyển khoản · 1.234.567 ₫');

    expect(columnTexts('Sự kiện')).toEqual([
      'Thanh toán đơn hàng',
      'Thanh toán đơn hàng',
      'Thông điệp hoá đơn bị từ chối',
      'Thông điệp e-mail bị từ chối',
    ]);
    expect(columnTexts('Nội dung')).toEqual([
      'Chuyển khoản · 1.234.567 ₫',
      'Voucher · —',
      'Thông điệp msg-51 bị từ chối: tác vụ không khớp với thông điệp',
      'Thông điệp msg-52 bị từ chối: tác vụ không tồn tại',
    ]);
    // Who recorded the payment is the actor of the event.
    expect(rowOfEvent(paid)).toHaveTextContent('manager@example.com');

    await switchLanguage(user, 'EN');
    await screen.findByText('Bank transfer · 1.234.567 ₫');
    expect(columnTexts('Event')).toEqual([
      'Order paid',
      'Order paid',
      'Invoice message rejected',
      'E-mail message rejected',
    ]);
    expect(columnTexts('Summary')).toEqual([
      'Bank transfer · 1.234.567 ₫',
      'Voucher · —',
      'Message msg-51 rejected: task does not match the message',
      'Message msg-52 rejected: task does not exist',
    ]);
  });

  it('says how many tasks a sweep failed for standing still and after how long, in both languages', async () => {
    // A run that says less than it should: no counts at all.
    const empty = systemEvent({
      logId: 150,
      level: 'Warning',
      eventType: 'processing.stale_tasks_failed',
      details: {},
    });
    serveSystemEvents([staleTasksFailedEvent, empty]);
    const { user } = renderLogs();
    await screen.findByText('2 tác vụ không có tiến triển trong 30 phút bị đánh dấu thất bại');

    expect(columnTexts('Sự kiện')).toEqual(['Tác vụ treo bị đánh dấu thất bại', 'Tác vụ treo bị đánh dấu thất bại']);
    expect(columnTexts('Nội dung')).toEqual([
      '2 tác vụ không có tiến triển trong 30 phút bị đánh dấu thất bại',
      '— tác vụ không có tiến triển trong — phút bị đánh dấu thất bại',
    ]);

    await switchLanguage(user, 'EN');
    await screen.findByText('2 tasks without progress for 30 min marked as failed');
    expect(columnTexts('Event')).toEqual(['Stale tasks failed', 'Stale tasks failed']);
    await settled();
  });

  it('says what a reconciliation run found and did, and which channels it could not read, in both languages', async () => {
    // A run that names less than it should: no counts for the cap, an entry without a reason, an entry that is none.
    const sparse = systemEvent({
      logId: 130,
      level: 'Warning',
      eventType: 'reconciliation.completed',
      details: { checked: 5, drifted: 0, tasksCreated: 0, unreadable: [{ channel: 'POS' }, 'junk'] },
    });
    // Nothing differs, and one task all the same: a failed sync that is repeated (and a count that is none).
    const repeated = systemEvent({
      logId: 127,
      level: 'Warning',
      eventType: 'reconciliation.completed',
      details: { checked: 8, drifted: 0, tasksCreated: 1, deferred: 0, failedRetried: 1, unreadable: [] },
    });
    const junkCount = systemEvent({
      logId: 126,
      level: 'Warning',
      eventType: 'reconciliation.completed',
      details: { checked: 8, drifted: 1, tasksCreated: 1, failedRetried: '2' },
    });
    const empty = systemEvent({ logId: 129, level: 'Warning', eventType: 'reconciliation.completed', details: {} });
    serveSystemEvents([reconciliationWithTroubleEvent, reconciliationEvent, sparse, empty, repeated, junkCount]);
    const { user } = renderLogs();
    await screen.findByText('12/1.340 sản phẩm lệch tồn · đã tạo 12 tác vụ đồng bộ');

    expect(columnTexts('Sự kiện')).toEqual(Array.from({ length: 6 }, () => 'Đối soát tồn kho'));
    expect(columnTexts('Mức độ')).toEqual(Array.from({ length: 6 }, () => 'Cảnh báo'));
    expect(columnTexts('Nội dung')).toEqual([
      // Failed syncs that were repeated and drift beyond the cap are said, and every channel that could not be read
      // by name: with the label of its error code, or the code as it is when this client has no label for it.
      '620/1.340 sản phẩm lệch tồn · đã tạo 500 tác vụ đồng bộ · chạy lại 3 đồng bộ đã lỗi · còn 123 để lượt sau · ' +
        'không đọc được: POS (kênh không khả dụng), Website (tls_handshake_failed)',
      // Nothing repeated, nothing deferred, every channel read: none of the parts is there.
      '12/1.340 sản phẩm lệch tồn · đã tạo 12 tác vụ đồng bộ',
      '0/5 sản phẩm lệch tồn · đã tạo 0 tác vụ đồng bộ · không đọc được: POS (—)',
      '—/— sản phẩm lệch tồn · đã tạo — tác vụ đồng bộ',
      '0/8 sản phẩm lệch tồn · đã tạo 1 tác vụ đồng bộ · chạy lại 1 đồng bộ đã lỗi',
      '1/8 sản phẩm lệch tồn · đã tạo 1 tác vụ đồng bộ',
    ]);
    // The run is found again by its correlation id: its tasks on Sync & Processing, its event here.
    expect(within(cellOf(reconciliationEvent, 'Liên kết')).getByRole('link', { name: 'Luồng' })).toHaveAttribute(
      'href',
      `/logs?${WINDOW}&correlationId=reconciliation-0199b3c27d1e7c4a9f00aabbccddeeff`,
    );

    await switchLanguage(user, 'EN');
    await screen.findByText('12 of 1.340 products differ · 12 sync tasks created');

    expect(columnTexts('Event')).toEqual(Array.from({ length: 6 }, () => 'Stock reconciliation'));
    expect(columnTexts('Summary')).toEqual([
      '620 of 1.340 products differ · 500 sync tasks created · 3 failed syncs repeated · 123 left for the next run · ' +
        'not readable: POS (channel unavailable), Website (tls_handshake_failed)',
      '12 of 1.340 products differ · 12 sync tasks created',
      '0 of 5 products differ · 0 sync tasks created · not readable: POS (—)',
      '— of — products differ · — sync tasks created',
      '0 of 8 products differ · 1 sync tasks created · 1 failed syncs repeated',
      '1 of 8 products differ · 1 sync tasks created',
    ]);
  });

  it('has a label for every error code of a channel call', async () => {
    const codes = [
      'channel_timeout',
      'channel_unavailable',
      'channel_rate_limited',
      'channel_rejected',
      'channel_circuit_open',
      // Not a code of a channel call: a read that failed with anything else (the reason is in the Workers log).
      'unexpected',
    ];
    serveSystemEvents([
      systemEvent({
        logId: 128,
        level: 'Warning',
        eventType: 'reconciliation.completed',
        details: {
          checked: 0,
          drifted: 0,
          tasksCreated: 0,
          deferred: 0,
          unreadable: codes.map((errorCode, index) => ({ channel: `K${index + 1}`, errorCode, pairs: 1 })),
        },
      }),
    ]);
    const { user } = renderLogs();

    expect(
      await screen.findByText(
        '0/0 sản phẩm lệch tồn · đã tạo 0 tác vụ đồng bộ · không đọc được: K1 (kênh không phản hồi), ' +
          'K2 (kênh không khả dụng), K3 (kênh giới hạn tần suất), K4 (kênh từ chối), K5 (tạm ngừng gọi sau nhiều lỗi), ' +
          'K6 (lỗi không mong đợi (xem log của Workers))',
      ),
    ).toBeInTheDocument();

    await switchLanguage(user, 'EN');
    expect(
      await screen.findByText(
        /K5 \(paused after repeated failures\), K6 \(unexpected error \(see the Workers log\)\)$/,
      ),
    ).toBeInTheDocument();
  });

  it('keeps a row inside a 1280 px window', async () => {
    serveSystemEvents(managerEvents);
    renderLogs();
    await loaded();

    const headers = [...document.querySelectorAll('thead th')].map((th) => th.textContent);
    expect(headers).toEqual(['Thời điểm', 'Mức độ', 'Sự kiện', 'Nội dung', 'Người thực hiện', 'Liên kết']);

    // jsdom has no layout: the fixed column widths are what can be checked. 1280 px window - 240 px menu - 2 × 24 px
    // padding = 992 px, minus a vertical scrollbar.
    const widths = [...document.querySelectorAll<HTMLElement>('table colgroup col')].map((col) =>
      Number.parseInt(col.style.width, 10),
    );
    expect(widths).toHaveLength(headers.length);
    expect(widths.every((width) => width > 0)).toBe(true);
    expect(widths.reduce((sum, width) => sum + width, 0)).toBeLessThanOrEqual(976);
  });
});

describe('logs: filters, sort and page in the address', () => {
  /** The list (45 events: three pages) opened with `address`, once its first answer is shown. */
  async function renderFiltered(address: string, totalCount = 45) {
    const { requests } = serveSystemEvents(managerEvents, totalCount);
    const app = renderLogs(address);
    await loaded();
    return { ...app, requests };
  }

  it('restores every filter, the sort direction and the page from the address (reload)', async () => {
    const address =
      `/logs?level=Error&level=Warning&eventType=processing.failed&eventType=inventory.imported` +
      `&correlationId=corr-sync-7&orderId=${ORDER_ID}&taskId=${SYNC_TASK_ID}` +
      `&from=2026-10-01&to=2026-10-02&sortDir=asc&page=2&pageSize=50`;
    const { requests, location } = await renderFiltered(address, 120);

    expect(requests).toHaveLength(1);
    const { from, to, ...rest } = requests[0]!.params;
    expect(rest).toEqual({
      page: '2',
      pageSize: '50',
      sortBy: 'createdAt',
      sortDir: 'asc',
      correlationId: 'corr-sync-7',
      orderId: ORDER_ID,
      taskId: SYNC_TASK_ID,
    });
    // One parameter per value, in the order of the API's lists whatever the order in the address.
    expect(requests[0]!.level).toEqual(['Warning', 'Error']);
    expect(requests[0]!.eventType).toEqual(['inventory.imported', 'processing.failed']);
    // The local start of the first day and the local end of the last day, written with the UTC offset.
    expect(from).toMatch(/^2026-10-01T00:00:00[+-]\d{2}:\d{2}$/);
    expect(new Date(from!).getTime()).toBe(new Date(2026, 9, 1).getTime());
    expect(new Date(to!).getTime()).toBe(new Date(2026, 9, 2, 23, 59, 59, 999).getTime());

    // The controls show the state of the address.
    expect(filterText('Mức độ')).toContain('Cảnh báo');
    expect(filterText('Mức độ')).toContain('Lỗi');
    expect(filterText('Loại sự kiện')).toContain('Nhập kho');
    expect(filterText('Loại sự kiện')).toContain('Tác vụ thất bại');
    expect(textFilter('Mã tương quan')).toHaveValue('corr-sync-7');
    expect(textFilter('ID đơn hàng')).toHaveValue(ORDER_ID);
    expect(textFilter('ID tác vụ')).toHaveValue(SYNC_TASK_ID);
    expect(startInput()).toHaveValue('01/10/2026');
    expect(endInput()).toHaveValue('02/10/2026');
    expect(document.querySelector('.ant-pagination-item-active')).toHaveTextContent('2');
    expect(sortOf(/Thời điểm/)).toBe('ascending');
    expect(location()).toBe(address);
  });

  it('level and event type take several values and start on page 1 again', async () => {
    const { user, location, requests } = await renderFiltered(`${BASE}&page=3`);
    expect(requests).toEqual([{ params: { ...BASE_PARAMS, page: '3' }, level: [], eventType: [] }]);

    await chooseOption(user, filter('Mức độ'), 'Lỗi');
    await waitFor(() => expect(requests.at(-1)?.level).toEqual(['Error']));
    expect(requests.at(-1)?.params).toEqual(BASE_PARAMS);
    expect(location()).toBe(`${BASE}&level=Error`);

    // Written in the order of the API's enum, whatever the order of choosing.
    await chooseOption(user, filter('Mức độ'), 'Cảnh báo');
    await waitFor(() => expect(requests.at(-1)?.level).toEqual(['Warning', 'Error']));
    expect(location()).toBe(`${BASE}&level=Warning&level=Error`);

    await chooseOption(user, filter('Loại sự kiện'), 'Tác vụ thất bại');
    await waitFor(() => expect(requests.at(-1)?.eventType).toEqual(['processing.failed']));
    expect(requests.at(-1)?.level).toEqual(['Warning', 'Error']);
    expect(location()).toBe(`${BASE}&level=Warning&level=Error&eventType=processing.failed`);

    await settled();
  });

  it('correlation id, order id and task id are exact texts and start on page 1 again', async () => {
    const { user, location, requests } = await renderFiltered(`${BASE}&page=3`);

    await user.type(textFilter('Mã tương quan'), ' corr-sync-7 ');
    await waitFor(() => expect(requests.at(-1)?.params).toEqual({ ...BASE_PARAMS, correlationId: 'corr-sync-7' }));
    expect(location()).toBe(`${BASE}&correlationId=corr-sync-7`);

    await user.click(textFilter('ID đơn hàng'));
    await user.paste(ORDER_ID);
    await waitFor(() => expect(requests.at(-1)?.params).toMatchObject({ orderId: ORDER_ID }));

    await user.click(textFilter('ID tác vụ'));
    await user.paste(SYNC_TASK_ID);
    await waitFor(() =>
      expect(requests.at(-1)?.params).toEqual({
        ...BASE_PARAMS,
        correlationId: 'corr-sync-7',
        orderId: ORDER_ID,
        taskId: SYNC_TASK_ID,
      }),
    );
    expect(location()).toBe(`${BASE}&correlationId=corr-sync-7&orderId=${ORDER_ID}&taskId=${SYNC_TASK_ID}`);

    await settled();
  });

  it('says that a typed id is no id and asks nothing with it', async () => {
    const { user, location, requests } = await renderFiltered(BASE);

    await user.type(textFilter('ID đơn hàng'), 'not-an-id');

    expect(await screen.findByTestId('logs-order-id-error')).toHaveTextContent('Không phải ID hợp lệ');
    expect(location()).toBe(`${BASE}&orderId=not-an-id`);
    // Nothing was asked, and the rows of the previous answer are not shown as if they matched.
    expect(requests).toHaveLength(1);
    expect(rows()).toHaveLength(0);
    expect(screen.getByRole('button', { name: /Làm mới/ })).toBeDisabled();
    expect(screen.queryByTestId('logs-task-id-error')).not.toBeInTheDocument();

    // Emptied again: the list is back.
    await user.clear(textFilter('ID đơn hàng'));
    await loaded();
    expect(screen.queryByTestId('logs-order-id-error')).not.toBeInTheDocument();
    expect(location()).toBe(BASE);

    await settled();
  });

  it('does not ask with an id from the address that is none', async () => {
    const { requests } = serveSystemEvents(managerEvents);
    renderLogs(`${BASE}&taskId=42`);

    expect(await screen.findByTestId('logs-task-id-error')).toBeInTheDocument();
    expect(textFilter('ID tác vụ')).toHaveValue('42');
    expect(requests).toHaveLength(0);
    expect(rows()).toHaveLength(0);

    await settled();
  });

  it('sorts by time only, newest first by default, and starts on page 1 again', async () => {
    const { user, location, requests } = await renderFiltered(`${BASE}&page=2`);
    expect(screen.getAllByRole('columnheader').filter((th) => th.hasAttribute('aria-sort'))).toHaveLength(1);

    // The default is newest first, so the first click asks for the oldest first.
    await user.click(screen.getByRole('columnheader', { name: /Thời điểm/ }));
    await waitFor(() =>
      expect(requests.at(-1)?.params).toEqual({ ...BASE_PARAMS, sortBy: 'createdAt', sortDir: 'asc' }),
    );
    expect(location()).toBe(`${BASE}&sortDir=asc`);
    expect(sortOf(/Thời điểm/)).toBe('ascending');

    // ... and the next one for the default again: nothing in the address.
    await user.click(screen.getByRole('columnheader', { name: /Thời điểm/ }));
    await waitFor(() => expect(requests.at(-1)?.params).toEqual(BASE_PARAMS));
    expect(location()).toBe(BASE);

    await settled();
  });

  it('keeps the page and the page size in the address', async () => {
    const { user, location, requests } = await renderFiltered(`${BASE}&level=Error`);

    await user.click(screen.getByRole('listitem', { name: '2' }));
    await waitFor(() => expect(requests.at(-1)?.params).toEqual({ ...BASE_PARAMS, page: '2' }));
    expect(requests.at(-1)?.level).toEqual(['Error']);
    expect(location()).toBe(`${BASE}&level=Error&page=2`);
    await waitFor(() => expect(document.querySelector('.ant-pagination-item-active')).toHaveTextContent('2'));

    await settled();
  });

  it('"Clear filters" removes every filter and counts the last 24 hours from now; the sort stays', async () => {
    const { user, location, requests } = await renderFiltered(
      `/logs?level=Error&eventType=processing.failed&correlationId=corr-sync-7&orderId=${ORDER_ID}` +
        `&taskId=${SYNC_TASK_ID}&from=2026-10-01&to=2026-10-02&sortDir=asc&page=2`,
    );

    await user.click(screen.getByRole('button', { name: 'Xoá bộ lọc' }));

    await waitFor(() => expect(location()).toMatch(/^\/logs\?sortDir=asc&since=[^&]+$/));
    await waitFor(() => expect(requests.at(-1)?.level).toEqual([]));
    const since = new URLSearchParams(location().split('?')[1]).get('since');
    expect(requests.at(-1)).toEqual({
      params: { page: '1', pageSize: '20', sortBy: 'createdAt', sortDir: 'asc', from: since },
      level: [],
      eventType: [],
    });
    expect(filterText('Mức độ')).not.toContain('Lỗi');
    expect(filterText('Loại sự kiện')).not.toContain('Tác vụ thất bại');
    expect(textFilter('Mã tương quan')).toHaveValue('');
    expect(textFilter('ID đơn hàng')).toHaveValue('');
    expect(textFilter('ID tác vụ')).toHaveValue('');
    expect(startInput()).toHaveValue('');
    expect(preset('24 giờ qua')).toBeChecked();

    await settled();
  });

  it('ignores address values that are not valid and never sends them', async () => {
    const { requests } = serveSystemEvents(managerEvents);
    renderLogs(`${BASE}&level=Fatal&level=Error,Warning&eventType=order.shipped&range=year&sortBy=level&page=x`);
    await loaded();

    expect(requests).toEqual([{ params: BASE_PARAMS, level: [], eventType: [] }]);
    expect(sortOf(/Thời điểm/)).toBe('descending');
    expect(preset('24 giờ qua')).toBeChecked();
  });

  it('shows the trace of an event: every filter is replaced by its correlation id, the window stays', async () => {
    const { user, location, requests } = await renderFiltered(
      `/logs?range=7d&level=Error&eventType=processing.failed&taskId=${SYNC_TASK_ID}&sortDir=asc&page=2&pageSize=50`,
    );

    await user.click(within(cellOf(processingFailedEvent, 'Liên kết')).getByRole('link', { name: 'Luồng' }));

    await waitFor(() => expect(location()).toBe('/logs?range=7d&sortDir=asc&pageSize=50&correlationId=corr-sync-7'));
    await waitFor(() => expect(requests.at(-1)?.params.correlationId).toBe('corr-sync-7'));
    const { from, to, ...rest } = requests.at(-1)!.params;
    expect(rest).toEqual({
      page: '1',
      pageSize: '50',
      sortBy: 'createdAt',
      sortDir: 'asc',
      correlationId: 'corr-sync-7',
    });
    expect(new Date(from!).getTime()).toBe(dayjs().subtract(6, 'day').startOf('day').valueOf());
    expect(to).toBeDefined();
    expect(requests.at(-1)?.level).toEqual([]);
    expect(requests.at(-1)?.eventType).toEqual([]);
    // The filter says which flow is shown.
    await waitFor(() => expect(textFilter('Mã tương quan')).toHaveValue('corr-sync-7'));
    expect(textFilter('ID tác vụ')).toHaveValue('');
    expect(preset('7 ngày qua')).toBeChecked();

    await settled();
  });
});

describe('logs: refresh and errors', () => {
  it('asks again on "Refresh" and whenever the page is opened', async () => {
    const { requests } = serveSystemEvents(managerEvents);
    const { user, router } = renderLogs();
    await loaded();

    await user.click(screen.getByRole('button', { name: /Làm mới/ }));
    await waitFor(() => expect(requests).toHaveLength(2));
    // The same window: events that arrived in the meantime are part of it.
    expect(requests[1]).toEqual(requests[0]);

    // Away, to an address without a page (the shell without any request of its own), and back through the menu.
    await act(() => router.navigate('/no-such-page'));
    await screen.findByText('Không tìm thấy trang');
    await user.click(screen.getByRole('link', { name: 'Nhật ký / kiểm toán' }));
    await loaded();
    await waitFor(() => expect(requests).toHaveLength(3));

    await settled();
  });

  it('shows new events after "Refresh"', async () => {
    const { state } = serveSystemEvents([importedEvent]);
    const { user } = renderLogs();
    await loaded();
    expect(rows()).toHaveLength(1);

    state.items = [orderRejectedEvent, importedEvent];
    await user.click(screen.getByRole('button', { name: /Làm mới/ }));

    expect(await screen.findByText('Thông điệp msg-17 bị từ chối: không phải JSON hợp lệ')).toBeInTheDocument();
    expect(rows()).toHaveLength(2);
  });

  it('shows a rejection of the API above the table', async () => {
    const { state } = serveSystemEvents(managerEvents);
    state.answer = () => problem(403);
    renderLogs();

    expect(await screen.findByRole('alert')).toHaveTextContent('Bạn không có quyền thực hiện thao tác này.');
    expect(screen.queryByTestId('logs-range-error')).not.toBeInTheDocument();
    expect(rows()).toHaveLength(0);
  });

  it('opens the order of an event from its link', async () => {
    serveSystemEvents(managerEvents);
    server.use(http.get(api('/orders/:id'), () => problem(404, { errorCode: 'order_not_found' })));
    const { user, location } = renderLogs();
    await loaded();

    await user.click(within(cellOf(insufficientEvent, 'Liên kết')).getByRole('link', { name: 'Đơn hàng' }));

    await waitFor(() => expect(location()).toBe(`/orders/${ORDER_ID}`));
    expect(await screen.findByText('Không tìm thấy đơn hàng')).toBeInTheDocument();
  });
});

describe('logs: language', () => {
  it('changes text only: the address, the rows, the filters and the fixed widths stay', async () => {
    const { requests } = serveSystemEvents(everyEventType, 45);
    const address = `${BASE}&level=Error&correlationId=corr-sync-7&page=2`;
    const { user, location } = renderLogs(address, adminUser);
    await loaded();
    const widths = () =>
      ['Mức độ', 'Level', 'Loại sự kiện', 'Event type', 'Mã tương quan', 'Correlation ID']
        .map((name) => screen.queryByLabelText(name)?.closest<HTMLElement>('.ant-select, .ant-input-affix-wrapper'))
        .filter((element) => element !== null && element !== undefined)
        .map((element) => element.style.width);
    const before = widths();
    expect(before).toHaveLength(3);

    await expectLanguageSwitchChangesTextOnly(user, location, {
      vietnamese: ['Nhật ký / kiểm toán', 'Thời điểm', 'Nội dung', 'Tài khoản bị khoá', 'Luồng'],
      english: ['Logs / audit', 'Time', 'Summary', 'Account locked', 'Trace'],
      requestCount: () => requests.length,
    });

    expect(widths()).toEqual(before);
    expect(location()).toBe(address);
    expect(userLockedEvent.eventType).toBe('user.locked');
  });
});
