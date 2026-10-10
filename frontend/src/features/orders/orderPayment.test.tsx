import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import type { TaskStatus, TaskType } from '@/features/processing/processingApi';
import { FLUSH_WINDOW_MS } from '@/features/realtime/hintTags';
import { fakeHub } from '@/test/hub';
import { chooseOption, findDialog, settled, switchLanguage, type AppUser } from '@/test/interactions';
import { attempt, ORDER_ID, orderDetail, orderSummary, serveChannels } from '@/test/orders';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';
import type { OrderDetail, OrderTask } from './ordersApi';

const INVOICE_TASK_ID = '0199a1b2-0000-7000-8000-0000000000c1';
const EMAIL_TASK_ID = '0199a1b2-0000-7000-8000-0000000000c2';
const OTHER_ORDER_ID = '0199a1b2-0000-7000-8000-000000000002';
const INVOICE_NUMBER = 'SIM-202610-3F9A1C0B7D2E4A68';
const emptyPage = { items: [], page: 1, pageSize: 20, totalCount: 0 };
const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

function paymentTask(taskType: TaskType, taskId: string, status: TaskStatus, overrides: Partial<OrderTask> = {}) {
  return {
    taskId,
    taskType,
    status,
    retryCount: 0,
    correlationId: 'corr-payment-1',
    messageId: `message-${taskType}`,
    createdAt: new Date(2026, 9, 9, 14, 30).toISOString(),
    updatedAt: new Date(2026, 9, 9, 14, 30, 2).toISOString(),
    attempts: status === 'Pending' ? [] : [attempt({ workerName: `${taskType}Worker` })],
    ...overrides,
  } satisfies OrderTask;
}

const unpaid = orderDetail();

/** A paid order whose invoice was issued and whose confirmation was sent. */
function paidOrder(overrides: Partial<OrderDetail> = {}): OrderDetail {
  return orderDetail({
    paymentStatus: 'Paid',
    payment: {
      status: 'Paid',
      paidAt: new Date(2026, 9, 9, 14, 30).toISOString(),
      paidByEmail: 'manager@example.com',
      method: 'BankTransfer',
      reference: 'FT26100912345',
    },
    invoice: { number: INVOICE_NUMBER, issuedAt: new Date(2026, 9, 9, 14, 31).toISOString() },
    tasks: [
      ...orderDetail().tasks,
      paymentTask('Invoice', INVOICE_TASK_ID, 'Succeeded'),
      paymentTask('Email', EMAIL_TASK_ID, 'Succeeded'),
    ],
    ...overrides,
  });
}

/**
 * `GET /api/orders/{id}` and `POST /api/orders/{id}/payment`. `order` is what the next read returns; `payAnswer`
 * decides the answer of a payment (the default records it: the order is the paid one from then on).
 */
function serveOrder(initial: OrderDetail = unpaid) {
  const state: { order: OrderDetail; payAnswer?: () => Response | Promise<Response> } = { order: initial };
  const reads: string[] = [];
  const payments: unknown[] = [];
  server.use(
    http.get(api('/orders/:id'), ({ params }) => {
      reads.push(String(params.id));
      return HttpResponse.json(state.order);
    }),
    http.post(api('/orders/:id/payment'), async ({ request }) => {
      payments.push(await request.json());
      if (state.payAnswer) {
        return state.payAnswer();
      }

      state.order = paidOrder();
      return HttpResponse.json({
        orderId: ORDER_ID,
        paymentStatus: 'Paid',
        paidAt: state.order.payment.paidAt,
        invoiceTaskId: INVOICE_TASK_ID,
        emailTaskId: EMAIL_TASK_ID,
      });
    }),
  );
  return { state, reads, payments };
}

function renderDetail(user: AuthUser = managerUser, options: { animations?: boolean } = {}) {
  return renderApp({ route: `/orders/${ORDER_ID}`, session: sessionOf(user), ...options });
}

const pageLoaded = () => screen.findByRole('heading', { name: /^(Đơn hàng|Order) WEB-20261001-ABC123$/ });
const card = () => screen.getByTestId('order-payment');
const payButton = () => within(card()).queryByRole('button', { name: /Ghi nhận thanh toán|Mark as paid/ });

/** The cell next to a label of the payment summary. */
function cellOf(label: string): HTMLElement {
  const cell = within(card())
    .getAllByText(label)
    .find((element) => element.closest('th.ant-descriptions-item-label'))
    ?.closest('th')?.nextElementSibling;
  if (!cell) {
    throw new Error(`No payment value for "${label}"`);
  }

  return cell as HTMLElement;
}

const valueOf = (label: string) => cellOf(label).textContent ?? '';
const labels = () => [...card().querySelectorAll('th.ant-descriptions-item-label')].map((th) => th.textContent);

async function openDialog(user: AppUser): Promise<HTMLElement> {
  await user.click(payButton()!);
  return findDialog(/^(Ghi nhận thanh toán đơn hàng|Mark order as paid)$/);
}

const methodSelect = (dialog: HTMLElement) =>
  within(dialog).getByRole('combobox', { name: /Phương thức thanh toán|Payment method/ });
const referenceInput = (dialog: HTMLElement) =>
  within(dialog).getByRole<HTMLInputElement>('textbox', { name: /Mã tham chiếu thanh toán|Payment reference/ });
const okButton = (dialog: HTMLElement) =>
  within(dialog).getByRole('button', { name: /Ghi nhận thanh toán|Mark as paid/ });

describe('order detail: payment card', () => {
  it.each([
    ['a Manager', managerUser, true],
    ['an Admin', adminUser, true],
    ['Staff', staffUser, false],
  ])(
    'shows an unpaid reserved order to %s - with "Mark as paid" for Manager and Admin only',
    async (_who, user, pays) => {
      serveOrder();
      renderDetail(user);
      await pageLoaded();

      expect(within(card()).getByText('Thanh toán')).toBeInTheDocument();
      expect(labels()).toEqual(['Trạng thái thanh toán']);
      expect(valueOf('Trạng thái thanh toán')).toBe('Chưa thanh toán');
      expect(within(card()).getByText('Chưa thanh toán').closest('.ant-tag')).toHaveClass('ant-tag-default');
      expect(payButton() !== null).toBe(pays);
      // Nothing explains a missing button here: the order can be paid, just not by this role.
      expect(within(card()).queryByText('Chỉ đơn hàng đã giữ hàng mới thanh toán được')).not.toBeInTheDocument();
      if (pays) {
        // A fixed width: the label differs per language.
        expect(payButton()).toHaveStyle({ width: '176px' });
      }
    },
  );

  it('offers no payment for an order without reserved stock: Manager reads why, Staff the status only', async () => {
    const insufficient = orderDetail({ status: 'InsufficientStock' });
    serveOrder(insufficient);
    const first = renderDetail(managerUser);
    await pageLoaded();
    expect(valueOf('Trạng thái thanh toán')).toBe('Chưa thanh toán');
    expect(payButton()).toBeNull();
    expect(within(card()).getByText('Chỉ đơn hàng đã giữ hàng mới thanh toán được')).toBeInTheDocument();
    first.unmount();

    serveOrder(insufficient);
    renderDetail(staffUser);
    await pageLoaded();
    expect(valueOf('Trạng thái thanh toán')).toBe('Chưa thanh toán');
    expect(payButton()).toBeNull();
    expect(within(card()).queryByText('Chỉ đơn hàng đã giữ hàng mới thanh toán được')).not.toBeInTheDocument();
  });

  it('shows a paid order with its invoice and its confirmation e-mail, in both languages', async () => {
    const { reads } = serveOrder(paidOrder());
    // Staff: every role reads the payment.
    const { user } = renderDetail(staffUser);
    await pageLoaded();

    expect(labels()).toEqual([
      'Trạng thái thanh toán',
      'Phương thức thanh toán',
      'Thanh toán lúc',
      'Người ghi nhận',
      'Mã tham chiếu thanh toán',
      'Hoá đơn',
      'E-mail xác nhận',
    ]);
    expect(valueOf('Trạng thái thanh toán')).toBe('Đã thanh toán');
    expect(within(card()).getByText('Đã thanh toán').closest('.ant-tag')).toHaveClass('ant-tag-success');
    expect(valueOf('Phương thức thanh toán')).toBe('Chuyển khoản');
    expect(valueOf('Thanh toán lúc')).toBe('09/10/2026 14:30');
    expect(valueOf('Người ghi nhận')).toBe('manager@example.com');
    expect(valueOf('Mã tham chiếu thanh toán')).toBe('FT26100912345');
    // The invoice number is shown in full with a copy button.
    expect(valueOf('Hoá đơn')).toBe(`${INVOICE_NUMBER}Xuất lúc 09/10/2026 14:31`);
    expect(
      within(card()).getByText(INVOICE_NUMBER).closest('.ant-typography')?.querySelector('.ant-typography-copy'),
    ).not.toBeNull();
    // The confirmation: the state of its task, with the way to the task.
    expect(valueOf('E-mail xác nhận')).toBe('Thành côngChi tiết tác vụ');
    expect(within(cellOf('E-mail xác nhận')).getByText('Thành công').closest('.ant-tag')).toHaveClass(
      'ant-tag-success',
    );
    expect(within(cellOf('E-mail xác nhận')).getByRole('link', { name: 'Chi tiết tác vụ' })).toHaveAttribute(
      'href',
      `/processing/tasks/${EMAIL_TASK_ID}`,
    );
    // A paid order is not paid again, and the labels keep their fixed width.
    expect(payButton()).toBeNull();
    expect(card().querySelector('th')).toHaveStyle({ width: '220px' });

    await switchLanguage(user, 'EN');
    await screen.findByRole('heading', { name: 'Order WEB-20261001-ABC123' });
    expect(labels()).toEqual([
      'Payment status',
      'Payment method',
      'Paid at',
      'Recorded by',
      'Payment reference',
      'Invoice',
      'Confirmation e-mail',
    ]);
    expect(valueOf('Payment status')).toBe('Paid');
    expect(valueOf('Payment method')).toBe('Bank transfer');
    expect(valueOf('Paid at')).toBe('09/10/2026 14:30');
    expect(valueOf('Invoice')).toBe(`${INVOICE_NUMBER}Issued at 09/10/2026 14:31`);
    expect(valueOf('Confirmation e-mail')).toBe('SucceededTask detail');
    // A language switch asks the API nothing.
    expect(reads).toEqual([ORDER_ID]);
  });

  it('shows the state of the invoice task, with the way to it, while there is no invoice yet', async () => {
    serveOrder(
      paidOrder({
        invoice: null,
        tasks: [paymentTask('Invoice', INVOICE_TASK_ID, 'InProgress'), paymentTask('Email', EMAIL_TASK_ID, 'Pending')],
      }),
    );
    renderDetail(staffUser);
    await pageLoaded();

    expect(valueOf('Hoá đơn')).toBe('Đang xử lýChi tiết tác vụ');
    expect(within(cellOf('Hoá đơn')).getByRole('link', { name: 'Chi tiết tác vụ' })).toHaveAttribute(
      'href',
      `/processing/tasks/${INVOICE_TASK_ID}`,
    );
    // No number and no issue time before the invoice exists.
    expect(cellOf('Hoá đơn')).not.toHaveTextContent('Xuất lúc');
    expect(card().querySelector('.ant-typography-copy')).toBeNull();
    expect(valueOf('E-mail xác nhận')).toBe('Đang chờChi tiết tác vụ');
  });

  it('says that no confirmation was sent when the order has no e-mail task, in both languages', async () => {
    serveOrder(paidOrder({ tasks: [paymentTask('Invoice', INVOICE_TASK_ID, 'Succeeded')] }));
    const { user } = renderDetail(staffUser);
    await pageLoaded();

    expect(valueOf('E-mail xác nhận')).toBe('Không gửi — khách hàng chưa có địa chỉ e-mail khi đơn được thanh toán');
    expect(within(cellOf('E-mail xác nhận')).queryByRole('link')).not.toBeInTheDocument();
    // The invoice does not depend on the e-mail.
    expect(cellOf('Hoá đơn')).toHaveTextContent(INVOICE_NUMBER);

    await switchLanguage(user, 'EN');
    await screen.findByRole('heading', { name: 'Order WEB-20261001-ABC123' });
    expect(valueOf('Confirmation e-mail')).toBe(
      'Not sent — the customer had no e-mail address when the order was paid',
    );
  });

  it('shows a failed confirmation as failed, with the way to its task - and the invoice untouched', async () => {
    serveOrder(
      paidOrder({
        tasks: [
          paymentTask('Invoice', INVOICE_TASK_ID, 'Succeeded'),
          paymentTask('Email', EMAIL_TASK_ID, 'Failed', {
            attempts: [attempt({ status: 'Failed', workerName: 'NotificationWorker', errorCode: 'email_rejected' })],
          }),
        ],
      }),
    );
    renderDetail(staffUser);
    await pageLoaded();

    expect(valueOf('E-mail xác nhận')).toBe('Thất bạiChi tiết tác vụ');
    expect(within(cellOf('E-mail xác nhận')).getByText('Thất bại').closest('.ant-tag')).toHaveClass('ant-tag-error');
    expect(within(cellOf('E-mail xác nhận')).getByRole('link', { name: 'Chi tiết tác vụ' })).toHaveAttribute(
      'href',
      `/processing/tasks/${EMAIL_TASK_ID}`,
    );
    expect(valueOf('Trạng thái thanh toán')).toBe('Đã thanh toán');
    expect(cellOf('Hoá đơn')).toHaveTextContent(INVOICE_NUMBER);
    // The attempt's error code stays as the API sends it, in the task block below.
    expect(within(screen.getByTestId(`order-task-${EMAIL_TASK_ID}`)).getByText('email_rejected')).toBeInTheDocument();
  });

  it('picks the newest task of a type when the order has several', async () => {
    const OLDER_ID = '0199a1b2-0000-7000-8000-0000000000c0';
    serveOrder(
      paidOrder({
        invoice: null,
        tasks: [
          paymentTask('Invoice', INVOICE_TASK_ID, 'RetryScheduled', {
            createdAt: new Date(2026, 9, 9, 15, 0).toISOString(),
          }),
          paymentTask('Invoice', OLDER_ID, 'Failed'),
        ],
      }),
    );
    renderDetail(staffUser);
    await pageLoaded();

    expect(valueOf('Hoá đơn')).toBe('Chờ thử lạiChi tiết tác vụ');
    expect(within(cellOf('Hoá đơn')).getByRole('link')).toHaveAttribute('href', `/processing/tasks/${INVOICE_TASK_ID}`);
  });

  it('truncates a reference of 100 characters and shows dashes for what a payment does not have', async () => {
    const reference = 'R'.repeat(100);
    serveOrder(paidOrder({ payment: { ...paidOrder().payment, reference } }));
    const first = renderDetail(staffUser);
    await pageLoaded();
    // One truncated line; the full text is its tooltip.
    expect(within(cellOf('Mã tham chiếu thanh toán')).getByText(reference)).toHaveClass('ant-typography-ellipsis');
    first.unmount();

    // No reference was entered, and the user who recorded the payment is gone.
    serveOrder(paidOrder({ payment: { ...paidOrder().payment, reference: null, paidByEmail: null } }));
    renderDetail(staffUser);
    await pageLoaded();
    expect(valueOf('Mã tham chiếu thanh toán')).toBe('—');
    expect(valueOf('Người ghi nhận')).toBe('—');
  });
});

describe('order detail: "Mark as paid"', () => {
  it('shows the order number and the total, and asks for a method: nothing is sent without one', async () => {
    const { payments } = serveOrder();
    const { user } = renderDetail();
    await pageLoaded();
    const dialog = await openDialog(user);

    expect(dialog).toHaveTextContent('WEB-20261001-ABC123');
    expect(dialog).toHaveTextContent('1.234.567 ₫');
    // No default: a financial record is chosen, not accepted.
    expect(methodSelect(dialog).closest('.ant-select')).toHaveTextContent('Chọn phương thức');
    expect(dialog).toHaveTextContent('Số biên lai hoặc mã giao dịch chuyển khoản');

    await user.click(okButton(dialog));
    await waitFor(() => expect(dialog.querySelector('.ant-form-item-explain-error')).not.toBeNull());
    expect(payments).toEqual([]);
    expect(valueOf('Trạng thái thanh toán')).toBe('Chưa thanh toán');
  });

  it('offers the four methods of the API', async () => {
    serveOrder();
    const { user } = renderDetail();
    await pageLoaded();
    const dialog = await openDialog(user);

    await user.click(methodSelect(dialog));
    await waitFor(() =>
      expect(
        [...document.querySelectorAll('.ant-select-item-option-content')].map((option) => option.textContent),
      ).toEqual(['Tiền mặt', 'Chuyển khoản', 'Thẻ', 'Ví điện tử']),
    );
  });

  it('cannot take a reference longer than the limit of the API', async () => {
    serveOrder();
    const { user } = renderDetail();
    await pageLoaded();
    const dialog = await openDialog(user);

    await user.click(referenceInput(dialog));
    await user.paste('x'.repeat(101));

    expect(referenceInput(dialog)).toHaveAttribute('maxlength', '100');
    expect(referenceInput(dialog).value).toBe('x'.repeat(100));
  });

  it.each([
    ['the trimmed reference', '  FT-26100912345  ', { paymentMethod: 'Card', paymentReference: 'FT-26100912345' }],
    ['no reference when it is blank', '   ', { paymentMethod: 'Card' }],
    ['no reference when none was typed', '', { paymentMethod: 'Card' }],
  ])('sends the method and %s', async (_what, typed, body) => {
    const { payments } = serveOrder();
    const { user } = renderDetail();
    await pageLoaded();
    const dialog = await openDialog(user);

    await chooseOption(user, methodSelect(dialog), 'Thẻ');
    if (typed) {
      await user.type(referenceInput(dialog), typed);
    }

    await user.click(okButton(dialog));

    await waitFor(() => expect(valueOf('Trạng thái thanh toán')).toBe('Đã thanh toán'));
    expect(payments).toEqual([body]);
    await settled();
  });

  it('records the payment: says what was requested, closes, and shows the paid order after one more read', async () => {
    const { reads, payments } = serveOrder();
    const { user, store } = renderDetail();
    await pageLoaded();
    await settled(store);
    expect(reads).toHaveLength(1);
    const dialog = await openDialog(user);

    await chooseOption(user, methodSelect(dialog), 'Chuyển khoản');
    await user.click(okButton(dialog));

    expect(
      await screen.findByText('Đã ghi nhận thanh toán. Đã yêu cầu xuất hoá đơn; đã yêu cầu gửi e-mail xác nhận.'),
    ).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(valueOf('Trạng thái thanh toán')).toBe('Đã thanh toán'));
    expect(cellOf('Hoá đơn')).toHaveTextContent(INVOICE_NUMBER);
    expect(payButton()).toBeNull();

    await settled(store);
    expect(payments).toHaveLength(1);
    // The page behind the dialog was fetched again exactly once.
    expect(reads).toHaveLength(2);
  });

  it('says that no confirmation is sent when the customer has no e-mail address', async () => {
    const { state } = serveOrder();
    state.payAnswer = () => {
      state.order = paidOrder({ invoice: null, tasks: [paymentTask('Invoice', INVOICE_TASK_ID, 'Pending')] });
      return HttpResponse.json({
        orderId: ORDER_ID,
        paymentStatus: 'Paid',
        paidAt: state.order.payment.paidAt,
        invoiceTaskId: INVOICE_TASK_ID,
        emailTaskId: null,
      });
    };
    const { user, store } = renderDetail(adminUser);
    await pageLoaded();
    const dialog = await openDialog(user);

    await chooseOption(user, methodSelect(dialog), 'Tiền mặt');
    await user.click(okButton(dialog));

    expect(
      await screen.findByText(
        'Đã ghi nhận thanh toán. Đã yêu cầu xuất hoá đơn; không gửi e-mail xác nhận (khách hàng không có địa chỉ e-mail).',
      ),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(valueOf('E-mail xác nhận')).toBe('Không gửi — khách hàng chưa có địa chỉ e-mail khi đơn được thanh toán'),
    );
    expect(valueOf('Hoá đơn')).toBe('Đang chờChi tiết tác vụ');
    await settled(store);
  });

  it('removes the order list from the cache, so the list is read again with the payment', async () => {
    const listRequests = { count: 0 };
    const { state } = serveOrder();
    serveChannels();
    server.use(
      http.get(api('/customers'), () => HttpResponse.json(emptyPage)),
      http.get(api('/orders'), () => {
        listRequests.count += 1;
        const items = [orderSummary({ paymentStatus: state.order.paymentStatus })];
        return HttpResponse.json({ items, page: 1, pageSize: 20, totalCount: 1 });
      }),
    );
    const { user, store } = renderApp({ route: '/orders', session: sessionOf(managerUser) });
    // By text: a query by role and name walks the whole list page and can outlast its wait on a loaded machine.
    await user.click(await screen.findByText('WEB-20261001-ABC123'));
    await pageLoaded();
    const cachedLists = () => Object.keys(store.getState().api.queries).filter((key) => key.startsWith('listOrders'));
    expect(cachedLists()).toHaveLength(1);
    expect(listRequests.count).toBe(1);

    const dialog = await openDialog(user);
    await chooseOption(user, methodSelect(dialog), 'Ví điện tử');
    await user.click(okButton(dialog));
    await waitFor(() => expect(valueOf('Trạng thái thanh toán')).toBe('Đã thanh toán'));

    // Nobody shows the list: its entry is dropped instead of being fetched for a page that is not open.
    await waitFor(() => expect(cachedLists()).toHaveLength(0));
    expect(listRequests.count).toBe(1);

    await user.click(screen.getByRole('button', { name: /Quay lại/ }));
    expect(await screen.findByText('Đã thanh toán')).toBeInTheDocument();
    expect(listRequests.count).toBe(2);
    await settled(store);
  });

  it.each([
    ['order_already_paid', 'Đơn hàng này đã được thanh toán.', 'This order has already been paid.'],
    [
      'order_not_payable',
      'Chỉ có thể thanh toán đơn hàng đã giữ hàng.',
      'Only an order whose stock is reserved can be paid.',
    ],
  ])(
    'says a 409 %s above the form in the UI language and reads the order again',
    async (errorCode, vietnamese, english) => {
      const { state, reads, payments } = serveOrder();
      state.payAnswer = () => problem(409, { errorCode, detail: 'An English text of the server.' });
      const { user, store } = renderDetail();
      await pageLoaded();
      await settled(store);
      const dialog = await openDialog(user);
      await chooseOption(user, methodSelect(dialog), 'Tiền mặt');

      await user.click(okButton(dialog));
      expect(await within(dialog).findByRole('alert')).toHaveTextContent(vietnamese);
      // The dialog stays; what is behind it is stale and was fetched again.
      await settled(store);
      expect(reads).toHaveLength(2);
      expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();

      await switchLanguage(user, 'EN');
      await user.click(okButton(dialog));
      await waitFor(() => expect(within(dialog).getByRole('alert')).toHaveTextContent(english));
      await settled(store);
      expect(payments).toHaveLength(2);
      expect(reads).toHaveLength(3);
    },
  );

  it.each([
    ['vi', "'Phương thức thanh toán' phải có giá trị.", "'Mã tham chiếu thanh toán' chỉ được dài tối đa 100 ký tự."],
    ['en', "'Payment method' must not be empty.", "'Payment reference' must be at most 100 characters."],
  ])('puts the validation messages of the API (%s) on their fields', async (_language, onMethod, onReference) => {
    const { state, payments } = serveOrder();
    state.payAnswer = () =>
      problem(400, {
        errorCode: 'validation_failed',
        errors: { paymentMethod: [onMethod], paymentReference: [onReference] },
      });
    const { user, store } = renderDetail();
    await pageLoaded();
    const dialog = await openDialog(user);
    await chooseOption(user, methodSelect(dialog), 'Thẻ');
    await user.type(referenceInput(dialog), 'REF-1');

    await user.click(okButton(dialog));

    const methodItem = methodSelect(dialog).closest<HTMLElement>('.ant-form-item')!;
    const referenceItem = referenceInput(dialog).closest<HTMLElement>('.ant-form-item')!;
    expect(await within(methodItem).findByText(onMethod)).toBeInTheDocument();
    expect(within(referenceItem).getByText(onReference)).toBeInTheDocument();
    expect(dialog.querySelector('.ant-alert')).toBeNull();

    // The next attempt starts without the old messages: an accepted payment closes the dialog.
    state.payAnswer = undefined;
    await user.click(okButton(dialog));
    await waitFor(() => expect(valueOf('Trạng thái thanh toán')).toBe('Đã thanh toán'));
    expect(payments).toHaveLength(2);
    await settled(store);
  });

  it('shows what the JSON binder refuses above the form (keys that name no field)', async () => {
    const { state } = serveOrder();
    state.payAnswer = () =>
      problem(400, {
        errorCode: 'validation_failed',
        errors: {
          request: ['The request field is required.'],
          '$.paymentMethod': ['The JSON value could not be converted.'],
        },
      });
    const { user, store } = renderDetail();
    await pageLoaded();
    const dialog = await openDialog(user);
    await chooseOption(user, methodSelect(dialog), 'Thẻ');

    await user.click(okButton(dialog));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'The request field is required. The JSON value could not be converted.',
    );
    await settled(store);
  });

  it('sends one payment for a double click: the button is disabled while the request runs', async () => {
    const { state, payments } = serveOrder();
    let release = () => {};
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    const record = () => {
      state.order = paidOrder();
      return HttpResponse.json({
        orderId: ORDER_ID,
        paymentStatus: 'Paid',
        paidAt: state.order.payment.paidAt,
        invoiceTaskId: INVOICE_TASK_ID,
        emailTaskId: EMAIL_TASK_ID,
      });
    };
    state.payAnswer = async () => {
      await held;
      return record();
    };
    const { user, store } = renderDetail();
    await pageLoaded();
    const dialog = await openDialog(user);
    await chooseOption(user, methodSelect(dialog), 'Tiền mặt');

    await user.dblClick(okButton(dialog));
    await waitFor(() => expect(okButton(dialog)).toBeDisabled());
    expect(okButton(dialog)).toHaveClass('ant-btn-loading');
    await user.click(okButton(dialog));
    expect(payments).toHaveLength(1);

    release();
    await waitFor(() => expect(valueOf('Trạng thái thanh toán')).toBe('Đã thanh toán'));
    expect(payments).toHaveLength(1);
    await settled(store);
  });

  it('starts empty again when it is reopened, also before its close animation has finished', async () => {
    const { state, payments } = serveOrder();
    state.payAnswer = () => problem(409, { errorCode: 'order_not_payable' });
    // Animations on: in jsdom they never finish, so the closed dialog and its form stay mounted.
    const { user, store } = renderDetail(managerUser, { animations: true });
    await pageLoaded();
    let dialog = await openDialog(user);
    await chooseOption(user, methodSelect(dialog), 'Chuyển khoản');
    await user.type(referenceInput(dialog), 'TYPED-AND-CANCELLED');
    await user.click(okButton(dialog));
    await within(dialog).findByRole('alert');
    await user.click(within(dialog).getByRole('button', { name: 'Huỷ' }));

    dialog = await openDialog(user);
    await waitFor(() => expect(referenceInput(dialog)).toHaveValue(''));
    expect(methodSelect(dialog).closest('.ant-select')).toHaveTextContent('Chọn phương thức');
    expect(methodSelect(dialog).closest('.ant-select')).not.toHaveTextContent('Chuyển khoản');
    expect(dialog.querySelector('.ant-alert')).toBeNull();
    expect(payments).toHaveLength(1);
    await settled(store);
  });
});

describe('order.changed (a payment recorded in another session)', () => {
  const indicator = () => screen.getByTestId('connection-indicator');

  /** A page with a live connection, once it is idle (the connect itself refetches what is on screen). */
  async function live(route: string, ready: () => Promise<unknown>) {
    const hub = fakeHub({ autoAccept: true });
    const app = renderApp({ route, session: sessionOf(staffUser), createHub: hub.create });
    await ready();
    await waitFor(() => expect(indicator()).toHaveTextContent('Trực tiếp'));
    await pause(FLUSH_WINDOW_MS + 300);
    await settled(app.store);
    return { ...app, hub };
  }

  it('reads the open order again once, and shows the payment; a hint about another order asks nothing', async () => {
    const { state, reads } = serveOrder();
    const { hub, store } = await live(`/orders/${ORDER_ID}`, pageLoaded);
    const loaded = reads.length;

    await hub.latest().send([{ type: 'order.changed', orderId: OTHER_ORDER_ID }]);
    await pause(FLUSH_WINDOW_MS + 300);
    expect(reads).toHaveLength(loaded);

    // Two hints for the order within one window (the payment, then its tasks): one read each window at most.
    state.order = paidOrder();
    await hub.latest().send([{ type: 'order.changed', orderId: ORDER_ID }]);
    await waitFor(() => expect(valueOf('Trạng thái thanh toán')).toBe('Đã thanh toán'));
    expect(reads).toHaveLength(loaded + 1);
    await pause(FLUSH_WINDOW_MS + 300);
    await settled(store);
    expect(reads).toHaveLength(loaded + 1);
  });

  it('reads the open order list again once', async () => {
    const list = { count: 0, paymentStatus: 'Unpaid' as 'Unpaid' | 'Paid' };
    serveChannels();
    server.use(
      http.get(api('/customers'), () => HttpResponse.json(emptyPage)),
      http.get(api('/orders'), () => {
        list.count += 1;
        const items = [orderSummary({ paymentStatus: list.paymentStatus })];
        return HttpResponse.json({ items, page: 1, pageSize: 20, totalCount: 1 });
      }),
    );
    const { hub, store } = await live('/orders', () => screen.findByText('Chưa thanh toán'));
    const loaded = list.count;

    list.paymentStatus = 'Paid';
    await hub.latest().send([{ type: 'order.changed', orderId: ORDER_ID }]);
    expect(await screen.findByText('Đã thanh toán')).toBeInTheDocument();
    await pause(FLUSH_WINDOW_MS + 300);
    await settled(store);
    expect(list.count).toBe(loaded + 1);
    // The detail of that order is not on screen and was not asked for.
    expect(Object.keys(store.getState().api.queries).some((key) => key.startsWith('getOrder'))).toBe(false);
  });
});
