import { act, screen, waitFor, within } from '@testing-library/react';
import dayjs from 'dayjs';
import { delay, http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import { inventoryApi } from '@/features/inventory/inventoryApi';
import type { ProcessingTaskDetail } from '@/features/processing/processingApi';
import { FLUSH_WINDOW_MS } from '@/features/realtime/hintTags';
import type { HubFactory } from '@/features/realtime/hub';
import { productsApi } from '@/features/products/productsApi';
import {
  AN_ID,
  anCustomer,
  binhCustomer,
  chooseCustomer,
  chooseProduct,
  coffeeProduct,
  COFFEE_ID,
  serveCustomerCatalog,
  serveProductCatalog,
  teaProduct,
  TEA_ID,
} from '@/test/catalog';
import { expectLanguageSwitchChangesTextOnly, switchLanguage, type AppUser, settled } from '@/test/interactions';
import { fakeHub } from '@/test/hub';
import { attempt, channel, ORDER_ID, orderDetail, serveChannels, task, TASK_ID } from '@/test/orders';
import { renderApp } from '@/test/renderApp';
import { api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';
import { POLL_INTERVAL_MS, POLL_WINDOW_MS } from './components/OrderOutcome';
import { ordersApi, type OrderDetail, type SubmitOrderRequest } from './ordersApi';

type Slug = 'digital-platform' | 'website' | 'pos';

const emptyPage = { items: [], page: 1, pageSize: 20, totalCount: 0 };

const accepted = (duplicate = false) =>
  HttpResponse.json({ taskId: TASK_ID, status: 'Pending', correlationId: 'corr-task-1', duplicate }, { status: 202 });

/**
 * The endpoints the order-entry page uses to submit and follow an order: `POST /api/orders/{slug}`,
 * `GET /api/processing/tasks/{id}` and `GET /api/orders/{id}`. Every request is recorded; the `…Answer` functions decide
 * the responses and can be replaced by a test (`taskAnswer` gets the number of the request, starting at 1).
 */
interface OrderBackend {
  submissions: { slug: string; body: SubmitOrderRequest }[];
  /** Arrival time of every task request (`performance.now()`). */
  taskRequests: number[];
  orderRequests: string[];
  submitAnswer: () => Response;
  taskAnswer: (requestNo: number) => ProcessingTaskDetail | Response;
  orderAnswer: () => OrderDetail | Response;
  /** How long `GET /api/orders/{id}` takes. */
  orderDelayMs: number;
}

function serveOrderBackend() {
  const backend: OrderBackend = {
    submissions: [],
    taskRequests: [],
    orderRequests: [],
    submitAnswer: () => accepted(),
    taskAnswer: () => task({ status: 'Succeeded', orderId: ORDER_ID, attempts: [attempt()] }),
    orderAnswer: () => orderDetail(),
    orderDelayMs: 0,
  };
  server.use(
    http.post(api('/orders/:slug'), async ({ request, params }) => {
      backend.submissions.push({ slug: String(params.slug), body: (await request.json()) as SubmitOrderRequest });
      return backend.submitAnswer();
    }),
    http.get(api('/processing/tasks/:id'), () => {
      backend.taskRequests.push(performance.now());
      const answer = backend.taskAnswer(backend.taskRequests.length);
      return answer instanceof Response ? answer : HttpResponse.json(answer);
    }),
    http.get(api('/orders/:id'), async ({ params }) => {
      backend.orderRequests.push(String(params.id));
      await delay(backend.orderDelayMs);
      const answer = backend.orderAnswer();
      return answer instanceof Response ? answer : HttpResponse.json(answer);
    }),
  );
  return backend;
}

/** Channels, products and customers as the pickers and panels ask for them, plus the order endpoints. */
function serveEverything() {
  const channels = serveChannels();
  const products = serveProductCatalog([teaProduct, coffeeProduct]);
  const customers = serveCustomerCatalog([anCustomer, binhCustomer]);
  const backend = serveOrderBackend();
  /** Everything except the polling of a task. */
  const otherRequests = () =>
    channels.count +
    products.listRequests.length +
    products.detailRequests.length +
    customers.listRequests.length +
    customers.detailRequests.length +
    backend.submissions.length +
    backend.orderRequests.length;
  return { backend, products, customers, otherRequests };
}

/**
 * The lists a finished order makes stale, each watched by a subscription like an open page would have: the counters
 * show how often each was fetched (1 = the first load, 2 = invalidated once).
 */
function watchLists(
  store: ReturnType<typeof renderApp>['store'],
  products: { listRequests: Record<string, string>[] },
) {
  const counts = { orders: 0, inventory: 0, movements: 0 };
  server.use(
    http.get(api('/orders'), () => {
      counts.orders += 1;
      return HttpResponse.json(emptyPage);
    }),
    http.get(api('/inventory'), () => {
      counts.inventory += 1;
      return HttpResponse.json(emptyPage);
    }),
    http.get(api('/inventory/movements'), () => {
      counts.movements += 1;
      return HttpResponse.json(emptyPage);
    }),
  );
  void store.dispatch(ordersApi.endpoints.listOrders.initiate({ page: 1, pageSize: 20 }));
  void store.dispatch(inventoryApi.endpoints.listInventory.initiate({ page: 1, pageSize: 20 }));
  void store.dispatch(inventoryApi.endpoints.listMovements.initiate({ page: 1, pageSize: 20 }));
  // A product list other than the pickers' (page size 5), so its requests can be told apart.
  void store.dispatch(productsApi.endpoints.listProducts.initiate({ page: 1, pageSize: 5 }));
  return () => ({ ...counts, products: products.listRequests.filter((request) => request.pageSize === '5').length });
}

async function renderOrderEntry(user: AuthUser = staffUser, createHub?: HubFactory) {
  const app = renderApp({ route: '/order-entry', session: sessionOf(user), createHub });
  await screen.findByRole('heading', { name: 'Nhập đơn hàng' });
  return app;
}

const panelElement = (slug: Slug) => screen.getByTestId(`order-panel-${slug}`);
const panelOf = (slug: Slug) => within(panelElement(slug));
const externalOrderId = (slug: Slug) => panelOf(slug).getByLabelText<HTMLInputElement>('Mã đơn hàng bên ngoài');
const customerOf = (slug: Slug) => panelOf(slug).getByLabelText('Khách hàng');
const productOfLine = (slug: Slug, line: number) =>
  panelOf(slug).getByRole('combobox', { name: `Sản phẩm, dòng ${line}` });
const quantityOfLine = (slug: Slug, line: number) =>
  panelOf(slug).getByRole('spinbutton', { name: `Số lượng, dòng ${line}` });
const submitButton = (slug: Slug) => panelOf(slug).getByRole('button', { name: 'Tạo đơn hàng' });

/** What each picker of a panel shows: the label of its value, or its placeholder. */
const pickerTexts = (slug: Slug) =>
  [...panelElement(slug).querySelectorAll('.ant-select')].map((select) => select.textContent ?? '');

/** Customer An, 3 × tea. */
async function fillOrder(appUser: AppUser, slug: Slug, quantity = '3') {
  await chooseCustomer(appUser, customerOf(slug), 'Nguyễn Văn An');
  await chooseProduct(appUser, productOfLine(slug, 1), 'TEA-01 — Trà xanh');
  await appUser.type(quantityOfLine(slug, 1), quantity);
}

/** A second line: 2 × coffee. */
async function addCoffeeLine(appUser: AppUser, slug: Slug) {
  await appUser.click(panelOf(slug).getByRole('button', { name: /Thêm dòng/ }));
  await chooseProduct(appUser, productOfLine(slug, 2), 'COF-01 — Cà phê');
  await appUser.type(quantityOfLine(slug, 2), '2');
}

/** Fills and submits the order of a panel and returns its outcome view. */
async function submitOrder(appUser: AppUser, slug: Slug = 'website') {
  await fillOrder(appUser, slug);
  await appUser.click(submitButton(slug));
  return screen.findByTestId(`order-outcome-${slug}`);
}

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * For the tests that wait for a polling interval, a flush window or the 60-second polling window: the time is jumped
 * over instead of waited for. `setTimeout` and `Date` are fake; `shouldAdvanceTime` lets the fake clock follow real
 * time between the jumps, so the application (debounced pickers, AntD, user-event) behaves as usual.
 */
function useJumpableClock() {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true, toFake: ['setTimeout', 'clearTimeout', 'Date'] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });
}

const jump = (ms: number) =>
  act(() => {
    vi.advanceTimersByTime(ms);
  });

/** What `watchLists` counts after the first load, and after one invalidation. */
const LOADED_ONCE = { orders: 1, inventory: 1, movements: 1, products: 1 };
const LOADED_TWICE = { orders: 2, inventory: 2, movements: 2, products: 2 };

const succeededTask = () => task({ status: 'Succeeded', orderId: ORDER_ID, attempts: [attempt()] });
const runningTask = () => task({ status: 'InProgress', attempts: [attempt({ status: 'Running', finishedAt: null })] });

/** A task that is first not handed to the queue yet, then picked up by the worker, then finished. */
function answerInThreeSteps(backend: OrderBackend) {
  backend.taskAnswer = (requestNo) => {
    if (requestNo === 1) {
      return task({ status: 'Pending', message: { ...task().message!, status: 'Pending', publishedAt: null } });
    }

    return requestNo === 2 ? runningTask() : succeededTask();
  };
}

describe('order entry', () => {
  it('shows the three channel panels in order, each with its channel; an inactive or missing channel is a warning and the form stays usable', async () => {
    const { backend } = serveEverything();
    serveChannels([
      channel({ slug: 'digital-platform', name: 'Sàn Lazada' }),
      channel({ slug: 'website', name: 'Web cửa hàng', status: 'Inactive' }),
    ]);
    const { user: appUser } = await renderOrderEntry();
    await screen.findByText('Sàn Lazada');

    const panels = screen.getAllByTestId(/^order-panel-/);
    expect(panels.map((panel) => panel.getAttribute('data-testid'))).toEqual([
      'order-panel-digital-platform',
      'order-panel-website',
      'order-panel-pos',
    ]);
    const titles = panels.map((panel) => panel.querySelector('.ant-card-head')?.textContent);
    expect(titles).toEqual([
      'Digital Platform OnlineSàn Lazada',
      'WebsiteWeb cửa hàngKênh đang ngừng hoạt động',
      'POS (tại quầy)Kênh chưa được cấu hình',
    ]);

    // Each panel has its own generated external order id.
    expect(externalOrderId('digital-platform').value).toMatch(/^DP-\d{8}-[A-Z0-9]{6}$/);
    expect(externalOrderId('website').value).toMatch(/^WEB-\d{8}-[A-Z0-9]{6}$/);
    expect(externalOrderId('pos').value).toMatch(/^POS-\d{8}-[A-Z0-9]{6}$/);

    // The page does not decide whether a channel takes orders: the request is sent, and the API's answer is shown in
    // that panel.
    backend.submitAnswer = () => problem(404, { errorCode: 'channel_not_found' });
    await fillOrder(appUser, 'website');
    await appUser.click(submitButton('website'));

    expect(await panelOf('website').findByRole('alert')).toHaveTextContent(
      'Kênh bán không tồn tại hoặc đang ngừng hoạt động.',
    );
    expect(backend.submissions.map((submission) => submission.slug)).toEqual(['website']);
    expect(panelOf('digital-platform').queryByRole('alert')).not.toBeInTheDocument();
    expect(submitButton('pos')).toBeEnabled();
    expect(externalOrderId('pos')).toBeEnabled();
  });

  it('shows the channel name next to the panel title only when it says something else', async () => {
    serveEverything();
    const { user: appUser } = await renderOrderEntry();
    const titles = () =>
      screen.getAllByTestId(/^order-panel-/).map((panel) => panel.querySelector('.ant-card-head')?.textContent);

    // The seeded channels: two are called like their panel (compared without case), the third is not in Vietnamese.
    await waitFor(() => expect(titles()).toEqual(['Digital Platform Online', 'Website', 'POS (tại quầy)POS']));

    await switchLanguage(appUser, 'EN');
    await waitFor(() => expect(titles()).toEqual(['Digital Platform Online', 'Website', 'POS']));
  });

  it('submits the order of the website panel as it was filled in, and the outcome replaces the form', async () => {
    const { backend } = serveEverything();
    const { user: appUser } = await renderOrderEntry();

    // Generated when the panel opened: prefix of the channel, today's local date, six characters.
    const id = externalOrderId('website').value;
    expect(id).toMatch(new RegExp(`^WEB-${dayjs().format('YYYYMMDD')}-[A-Z0-9]{6}$`));

    await fillOrder(appUser, 'website');
    await addCoffeeLine(appUser, 'website');
    // No price and no total in the form: amounts come from the order.
    expect(panelElement('website')).not.toHaveTextContent('₫');
    await appUser.click(submitButton('website'));

    const outcome = await screen.findByTestId('order-outcome-website');
    expect(backend.submissions).toEqual([
      {
        slug: 'website',
        body: {
          externalOrderId: id,
          customerId: AN_ID,
          items: [
            { productId: TEA_ID, stock: 3 },
            { productId: COFFEE_ID, stock: 2 },
          ],
        },
      },
    ]);
    // The form is replaced by the outcome; no confirmation was asked for.
    expect(panelOf('website').queryByLabelText('Mã đơn hàng bên ngoài')).not.toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(outcome).toHaveTextContent(id);

    // The outcome is followed to its end before the test is over: a request still in flight would be answered by the
    // handlers of the next test and counted there.
    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();
    expect(backend.orderRequests).toEqual([ORDER_ID]);
  });

  it('says so when the channel had already accepted the order, and shows the existing task', async () => {
    const { backend } = serveEverything();
    backend.submitAnswer = () => accepted(true);
    const { user: appUser } = await renderOrderEntry();

    const outcome = await submitOrder(appUser);

    expect(
      await within(outcome).findByText('Đơn hàng này đã được tiếp nhận trước đó; không có xử lý mới nào được bắt đầu.'),
    ).toBeInTheDocument();
    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();
    expect(backend.orderRequests).toEqual([ORDER_ID]);
  });

  it('warns when the order was recorded without a reservation', async () => {
    const { backend } = serveEverything();
    backend.orderAnswer = () => orderDetail({ status: 'InsufficientStock' });
    backend.orderDelayMs = 700;
    const { user: appUser } = await renderOrderEntry();

    const outcome = await submitOrder(appUser);

    // The task has succeeded, the order is still on its way: nothing is called a success before the order says so
    // (found in the browser: a green "processed" showed for a moment).
    expect(await within(outcome).findByText('Thành công')).toBeInTheDocument();
    expect(outcome.querySelector('.ant-alert')).toBeNull();

    const warning = await within(outcome).findByText('Không đủ tồn kho; đơn hàng được ghi nhận nhưng không giữ hàng.');
    expect(warning.closest('.ant-alert')).toHaveClass('ant-alert-warning');
    expect(within(outcome).getByText('Không đủ hàng').closest('.ant-tag')).toHaveClass('ant-tag-warning');
    // The task itself succeeded: insufficient stock is a business outcome, not a failure.
    expect(within(outcome).getByText('Thành công')).toBeInTheDocument();
    expect(within(outcome).getByRole('link', { name: 'Mở đơn hàng' })).toBeInTheDocument();
    expect(within(outcome).queryByText('Đơn hàng này đã được tiếp nhận trước đó', { exact: false })).toBeNull();
  });

  it('shows a failed task with the error of its last attempt and the correlation id, never the message error', async () => {
    const { backend } = serveEverything();
    backend.taskAnswer = () =>
      task({
        status: 'Failed',
        attempts: [
          attempt({ attemptNo: 1, status: 'Failed', errorCode: 'db_unavailable', errorMessage: 'First failure.' }),
          attempt({
            attemptNo: 2,
            status: 'Failed',
            errorCode: 'reservation_failed',
            errorMessage: 'The reservation could not be written.',
          }),
        ],
        message: { ...task().message!, lastError: 'Host=db;Password=do-not-show' },
      });
    const { user: appUser } = await renderOrderEntry();

    const outcome = await submitOrder(appUser);

    const failure = await within(outcome).findByText('Xử lý đơn hàng thất bại.');
    const alert = failure.closest<HTMLElement>('.ant-alert')!;
    expect(alert).toHaveClass('ant-alert-error');
    expect(alert).toHaveTextContent('reservation_failed');
    expect(alert).toHaveTextContent('The reservation could not be written.');
    expect(alert).not.toHaveTextContent('First failure.');
    expect(within(alert).getByText('corr-task-1')).toBeInTheDocument();
    expect(alert.querySelector('.ant-typography-copy')).not.toBeNull();
    expect(within(outcome).getByText('Thất bại').closest('.ant-tag')).toHaveClass('ant-tag-error');
    expect(outcome).not.toHaveTextContent('do-not-show');
    // No order exists, so none is asked for; a failed task is finished and not polled.
    await pause(POLL_INTERVAL_MS + 700);
    expect(backend.orderRequests).toEqual([]);
    expect(backend.taskRequests).toHaveLength(1);
    expect(within(outcome).queryByRole('link', { name: 'Mở đơn hàng' })).not.toBeInTheDocument();
  });

  it('tells apart "not sent yet", "could not be sent" and "retrying", and never shows the message error', async () => {
    const { backend } = serveEverything();
    const unpublished = { ...task().message!, publishedAt: null, lastError: 'amqp://user:do-not-show@broker' };
    backend.taskAnswer = (requestNo) => {
      if (requestNo === 1) {
        return task({ status: 'Pending', message: { ...unpublished, status: 'Pending' } });
      }

      return requestNo === 2
        ? task({ status: 'Pending', message: { ...unpublished, status: 'Failed' } })
        : task({
            status: 'RetryScheduled',
            retryCount: 1,
            attempts: [attempt({ status: 'Failed', errorCode: 'db_unavailable', errorMessage: 'Database is down.' })],
          });
    };
    const { user: appUser } = await renderOrderEntry();

    const outcome = await submitOrder(appUser);

    const waiting = await within(outcome).findByText('Đơn hàng đang chờ được chuyển vào hàng đợi xử lý.');
    expect(waiting.closest('.ant-alert')).toHaveClass('ant-alert-info');
    expect(outcome).not.toHaveTextContent('do-not-show');

    const notSent = await within(outcome).findByText('Không gửi được đơn hàng vào hàng đợi xử lý.');
    expect(notSent.closest('.ant-alert')).toHaveClass('ant-alert-error');
    expect(outcome).not.toHaveTextContent('do-not-show');

    const retrying = await within(outcome).findByText('Xử lý gặp lỗi tạm thời; hệ thống đang thử lại.');
    expect(retrying.closest('.ant-alert')).toHaveClass('ant-alert-warning');
    expect(retrying.closest('.ant-alert')).toHaveTextContent('db_unavailable');
    expect(within(outcome).getByText('Chờ thử lại').closest('.ant-tag')).toHaveClass('ant-tag-warning');
    // None of these is a finished task: the lists are left alone and the order is not asked for.
    expect(backend.orderRequests).toEqual([]);
  });

  it('shows API errors where they belong, keeps the external order id for the resend, and "New order" gives a new one', async () => {
    const { backend } = serveEverything();
    const { user: appUser } = await renderOrderEntry();
    const panel = panelOf('website');
    const id = externalOrderId('website').value;
    await fillOrder(appUser, 'website');
    await addCoffeeLine(appUser, 'website');

    // 400: field errors on their fields and lines, the error of the list under the list.
    backend.submitAnswer = () =>
      problem(400, {
        errorCode: 'validation_failed',
        errors: {
          customerId: ["'Khách hàng' không tồn tại (máy chủ)."],
          'items[0].productId': ["Sản phẩm 'p-1' không tồn tại (máy chủ)."],
          'items[1].stock': ["'Số lượng' phải từ 1 đến 10000 (máy chủ)."],
          items: ["'Các dòng hàng' không được chứa cùng một sản phẩm nhiều lần (máy chủ)."],
        },
      });
    await appUser.click(submitButton('website'));

    const customerError = await panel.findByText("'Khách hàng' không tồn tại (máy chủ).");
    expect(customerError.closest('.ant-form-item')).toContainElement(customerOf('website'));
    const productError = panel.getByText("Sản phẩm 'p-1' không tồn tại (máy chủ).");
    expect(productError.closest('.ant-form-item')).toContainElement(productOfLine('website', 1));
    expect(productError.closest('.ant-form-item')).not.toContainElement(productOfLine('website', 2));
    const quantityError = panel.getByText("'Số lượng' phải từ 1 đến 10000 (máy chủ).");
    expect(quantityError.closest('.ant-form-item')).toContainElement(quantityOfLine('website', 2));
    expect(quantityError.closest('.ant-form-item')).not.toContainElement(quantityOfLine('website', 1));
    const listError = panel.getByText("'Các dòng hàng' không được chứa cùng một sản phẩm nhiều lần (máy chủ).");
    expect(listError.closest('.ant-form-item')).toContainElement(productOfLine('website', 1));
    expect(listError.closest('.ant-form-item')).toContainElement(quantityOfLine('website', 2));
    expect(panel.queryByRole('alert')).not.toBeInTheDocument();
    expect(externalOrderId('website')).toHaveValue(id);
    // The other panels are not touched by it.
    expect(screen.getAllByText("'Khách hàng' không tồn tại (máy chủ).")).toHaveLength(1);

    // 409: the number was used with other content - shown on the number, where a new one is chosen.
    backend.submitAnswer = () => problem(409, { errorCode: 'order_conflict' });
    await appUser.click(submitButton('website'));

    const conflict = await panel.findByText(
      'Mã đơn hàng này đã được dùng trên kênh này với nội dung khác. Hãy tạo số mới cho đơn hàng này.',
    );
    expect(conflict.closest('.ant-form-item')).toContainElement(externalOrderId('website'));
    expect(panel.queryByRole('alert')).not.toBeInTheDocument();
    // The errors of the previous attempt are gone - also the one of the list, which no field rule clears.
    await waitFor(() =>
      expect(
        [...panelElement('website').querySelectorAll('.ant-form-item-explain-error')].map((item) => item.textContent),
      ).toEqual([conflict.textContent]),
    );
    expect(externalOrderId('website')).toHaveValue(id);

    // 404: the channel - shown in the panel's alert.
    backend.submitAnswer = () => problem(404, { errorCode: 'channel_not_found' });
    await appUser.click(submitButton('website'));

    expect(await panel.findByRole('alert')).toHaveTextContent('Kênh bán không tồn tại hoặc đang ngừng hoạt động.');
    expect(externalOrderId('website')).toHaveValue(id);
    expect(screen.queryByTestId('order-outcome-website')).not.toBeInTheDocument();

    // Every attempt was the same order: the same external order id, the same content.
    expect(backend.submissions.map((submission) => submission.body.externalOrderId)).toEqual([id, id, id]);

    // Accepted at last; "New order" starts an empty form with a new number.
    backend.submitAnswer = () => accepted();
    await appUser.click(submitButton('website'));
    const outcome = await screen.findByTestId('order-outcome-website');
    expect(backend.submissions.at(-1)?.body.externalOrderId).toBe(id);
    await appUser.click(within(outcome).getByRole('button', { name: 'Đơn hàng mới' }));

    const next = (await panel.findByLabelText<HTMLInputElement>('Mã đơn hàng bên ngoài')).value;
    expect(next).toMatch(/^WEB-\d{8}-[A-Z0-9]{6}$/);
    expect(next).not.toBe(id);
    expect(screen.queryByTestId('order-outcome-website')).not.toBeInTheDocument();
    expect(pickerTexts('website')).toEqual(['Chọn khách hàng', 'Chọn sản phẩm']);
    expect(panel.getAllByRole('spinbutton')).toHaveLength(1);
    expect(quantityOfLine('website', 1)).toHaveValue('');
    expect(panel.queryByRole('alert')).not.toBeInTheDocument();

    await settled();
  });

  it('lets the user type the external order id or ask for a new number, and sends it as it is', async () => {
    const { backend } = serveEverything();
    const { user: appUser } = await renderOrderEntry();
    const generated = externalOrderId('pos').value;

    await appUser.click(panelOf('pos').getByRole('button', { name: 'Tạo số mới' }));
    const renewed = externalOrderId('pos').value;
    expect(renewed).toMatch(/^POS-\d{8}-[A-Z0-9]{6}$/);
    expect(renewed).not.toBe(generated);

    // A number typed by the user (the previous number of a channel, to try a duplicate or a conflict).
    await appUser.clear(externalOrderId('pos'));
    await appUser.type(externalOrderId('pos'), 'POS-TYPED-1');
    const outcome = await submitOrder(appUser, 'pos');

    expect(backend.submissions).toHaveLength(1);
    expect(backend.submissions[0]).toMatchObject({ slug: 'pos', body: { externalOrderId: 'POS-TYPED-1' } });
    expect(outcome).toHaveTextContent('POS-TYPED-1');

    // The outcome is followed to its end (task, then order) before the test is over.
    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();
    await settled();
  });

  it('asks for the required fields before anything is sent, and the last line cannot be removed', async () => {
    const { backend } = serveEverything();
    const { user: appUser } = await renderOrderEntry();
    const panel = panelOf('digital-platform');

    await appUser.clear(externalOrderId('digital-platform'));
    await appUser.click(submitButton('digital-platform'));

    expect(await panel.findByText('Hãy nhập thông tin cho trường Mã đơn hàng bên ngoài')).toBeInTheDocument();
    expect(panel.getByText('Hãy nhập thông tin cho trường Khách hàng')).toBeInTheDocument();
    expect(panel.getByText('Hãy nhập thông tin cho trường Sản phẩm')).toBeInTheDocument();
    expect(panel.getByText('Hãy nhập thông tin cho trường Số lượng')).toBeInTheDocument();
    expect(backend.submissions).toEqual([]);
    expect(panel.getByRole('button', { name: 'Xoá dòng 1' })).toBeDisabled();
    // Fixed widths for what differs per language.
    expect(submitButton('digital-platform')).toHaveStyle({ width: '176px' });
  });

  it('keeps the panels independent: an order in one panel leaves the input of the others alone', async () => {
    const { backend } = serveEverything();
    const { user: appUser } = await renderOrderEntry();
    const digitalId = externalOrderId('digital-platform').value;
    await appUser.clear(externalOrderId('pos'));
    await appUser.type(externalOrderId('pos'), 'POS-KEEP-ME');
    await chooseCustomer(appUser, customerOf('pos'), 'Trần Thị Bình');
    await chooseProduct(appUser, productOfLine('pos', 1), 'COF-01 — Cà phê');
    await appUser.type(quantityOfLine('pos', 1), '7');

    const outcome = await submitOrder(appUser, 'website');
    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();

    expect(backend.submissions.map((submission) => submission.slug)).toEqual(['website']);
    expect(externalOrderId('pos')).toHaveValue('POS-KEEP-ME');
    expect(pickerTexts('pos')).toEqual(['Trần Thị Bình', 'COF-01 — Cà phê']);
    expect(quantityOfLine('pos', 1)).toHaveValue('7');
    expect(externalOrderId('digital-platform')).toHaveValue(digitalId);
    expect(pickerTexts('digital-platform')).toEqual(['Chọn khách hàng', 'Chọn sản phẩm']);
    expect(screen.queryByTestId('order-outcome-pos')).not.toBeInTheDocument();

    // The product chosen in another panel is not disabled here: "already on another line" is per panel.
    await appUser.click(productOfLine('digital-platform', 1));
    await waitFor(() =>
      expect(
        [...document.querySelectorAll('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option')]
          .length,
      ).toBe(2),
    );
    expect(
      document.querySelectorAll(
        '.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option-disabled',
      ),
    ).toHaveLength(0);
  });

  it('is usable by every role', async () => {
    const { backend } = serveEverything();
    const { user: appUser } = await renderOrderEntry(managerUser);

    const outcome = await submitOrder(appUser, 'digital-platform');

    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();
    expect(backend.submissions[0]?.slug).toBe('digital-platform');
  });
});

// No live connection here (the default hub of the tests never connects): the task is polled, and the outcome view itself
// refreshes the lists.
describe('order entry: outcome by polling', () => {
  useJumpableClock();

  it('shows each state of the task: not handed to the queue yet, being processed, then the order with its status and total', async () => {
    const { backend } = serveEverything();
    answerInThreeSteps(backend);
    const { user: appUser } = await renderOrderEntry();

    const outcome = await submitOrder(appUser);

    // Not handed to the queue yet.
    expect(await within(outcome).findByText('Đơn hàng đang chờ được chuyển vào hàng đợi xử lý.')).toBeInTheDocument();
    expect(within(outcome).getByText('Đang chờ').closest('.ant-tag')).toHaveClass('ant-tag-default');

    // Picked up by the worker.
    jump(POLL_INTERVAL_MS + 100);
    expect(await within(outcome).findByText('Đơn hàng đang được xử lý.')).toBeInTheDocument();
    expect(within(outcome).getByText('Đang xử lý').closest('.ant-tag')).toHaveClass('ant-tag-processing');
    expect(backend.orderRequests).toEqual([]);

    // Finished: the order is loaded, with the status and the total the API computed.
    jump(POLL_INTERVAL_MS + 100);
    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();
    expect(within(outcome).getByText('Thành công').closest('.ant-tag')).toHaveClass('ant-tag-success');
    expect(within(outcome).getByText('Đã giữ hàng').closest('.ant-tag')).toHaveClass('ant-tag-processing');
    expect(outcome).toHaveTextContent('1.234.567 ₫');
    expect(within(outcome).getByRole('link', { name: 'Mở đơn hàng' })).toHaveAttribute('href', `/orders/${ORDER_ID}`);
    expect(within(outcome).getByRole('button', { name: 'Đơn hàng mới' })).toHaveStyle({ width: '176px' });
  });

  it('refreshes the lists once, when the task has finished; a finished task is not asked for again and the order is loaded once', async () => {
    const { backend, products } = serveEverything();
    answerInThreeSteps(backend);
    const { user: appUser, store } = await renderOrderEntry();
    const counts = watchLists(store, products);
    await waitFor(() => expect(counts()).toEqual(LOADED_ONCE));

    const outcome = await submitOrder(appUser);
    await within(outcome).findByText('Đơn hàng đang chờ được chuyển vào hàng đợi xử lý.');
    expect(counts()).toEqual(LOADED_ONCE);

    jump(POLL_INTERVAL_MS + 100);
    await within(outcome).findByText('Đơn hàng đang được xử lý.');
    expect(counts()).toEqual(LOADED_ONCE);

    // Orders, stock, movements and products were invalidated - once.
    jump(POLL_INTERVAL_MS + 100);
    await within(outcome).findByText('Đã giữ hàng cho đơn hàng.');
    await waitFor(() => expect(counts()).toEqual(LOADED_TWICE));

    // A finished task is not asked for again, the order was loaded once, and nothing is invalidated a second time.
    jump(POLL_INTERVAL_MS + 700);
    await pause(200);
    expect(backend.taskRequests).toHaveLength(3);
    expect(backend.orderRequests).toEqual([ORDER_ID]);
    expect(counts()).toEqual(LOADED_TWICE);
  });
});

describe('order entry: polling window', () => {
  useJumpableClock();

  afterEach(() => {
    // Back to jsdom's own value (see `setTabVisibility`).
    delete (document as { visibilityState?: unknown }).visibilityState;
  });

  /** What a browser does when the tab is hidden or shown. */
  function setTabVisibility(state: 'visible' | 'hidden') {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
    act(() => {
      document.dispatchEvent(new Event('visibilitychange'));
    });
  }

  /** An order whose task stays in the queue: the outcome view polls. */
  async function submitQueuedOrder() {
    const { backend } = serveEverything();
    backend.taskAnswer = () => task({ status: 'Pending' });
    const app = await renderOrderEntry();
    const outcome = await submitOrder(app.user);
    await within(outcome).findByText('Đơn hàng đang chờ trong hàng đợi xử lý.');
    return { ...app, backend, outcome };
  }

  it('stops asking after 60 seconds, says the order is still being processed, and "Check again" starts another window', async () => {
    const { backend, outcome, user: appUser } = await submitQueuedOrder();
    const stillProcessing = 'Đơn hàng vẫn đang được xử lý; đã dừng tự động kiểm tra.';
    expect(within(outcome).queryByText(stillProcessing)).not.toBeInTheDocument();

    // Polling runs while the window is open.
    const first = backend.taskRequests.length;
    jump(POLL_INTERVAL_MS + 100);
    await waitFor(() => expect(backend.taskRequests.length).toBeGreaterThan(first));

    jump(POLL_WINDOW_MS);
    expect(await within(outcome).findByText(stillProcessing)).toBeInTheDocument();
    // What is known about the task stays visible.
    expect(within(outcome).getByText('Đơn hàng đang chờ trong hàng đợi xử lý.')).toBeInTheDocument();
    await pause(200);
    const stopped = backend.taskRequests.length;

    // Nothing is asked any more.
    jump(10 * POLL_INTERVAL_MS);
    await pause(200);
    expect(backend.taskRequests).toHaveLength(stopped);

    // "Check again": one request at once, and polling for another 60 seconds. (The button is busy while a check is
    // running; it is found by its plain name once it is idle.)
    const checkAgain = await within(outcome).findByRole('button', { name: 'Kiểm tra lại' });
    expect(checkAgain).toHaveStyle({ width: '136px' });
    await appUser.click(checkAgain);
    await waitFor(() => expect(backend.taskRequests).toHaveLength(stopped + 1));
    expect(within(outcome).queryByText(stillProcessing)).not.toBeInTheDocument();

    await pause(200);
    jump(POLL_INTERVAL_MS + 100);
    await waitFor(() => expect(backend.taskRequests.length).toBeGreaterThanOrEqual(stopped + 2));

    jump(POLL_WINDOW_MS);
    expect(await within(outcome).findByText(stillProcessing)).toBeInTheDocument();

    // The worker caught up meanwhile: the next check shows the result and the polling is over for good.
    backend.taskAnswer = () => task({ status: 'Succeeded', orderId: ORDER_ID, attempts: [attempt()] });
    await appUser.click(await within(outcome).findByRole('button', { name: 'Kiểm tra lại' }));
    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();
    expect(within(outcome).queryByText(stillProcessing)).not.toBeInTheDocument();
    expect(within(outcome).queryByRole('button', { name: 'Kiểm tra lại' })).not.toBeInTheDocument();
  });

  it('does not ask while the tab is hidden and continues when it is shown again', async () => {
    const { backend } = await submitQueuedOrder();

    setTabVisibility('hidden');
    await pause(200);
    const whileHidden = backend.taskRequests.length;
    jump(10 * POLL_INTERVAL_MS);
    await pause(200);
    expect(backend.taskRequests).toHaveLength(whileHidden);

    setTabVisibility('visible');
    jump(POLL_INTERVAL_MS + 100);
    await waitFor(() => expect(backend.taskRequests.length).toBeGreaterThan(whileHidden));

    await settled();
  });

  it('stops asking when the page is left', async () => {
    const { backend, user: appUser } = await submitQueuedOrder();

    await appUser.click(screen.getByRole('link', { name: 'Kênh bán' }));
    await screen.findByRole('heading', { name: 'Kênh bán' });
    await pause(200);
    const afterLeaving = backend.taskRequests.length;

    jump(10 * POLL_INTERVAL_MS);
    await pause(200);
    expect(backend.taskRequests).toHaveLength(afterLeaving);
  });
});

describe('order entry: outcome by push', () => {
  useJumpableClock();

  const taskChanged = (status: string, orderId?: string) => ({
    type: 'processing.task.changed',
    taskId: TASK_ID,
    taskType: 'OrderProcessing',
    status,
    ...(orderId ? { orderId } : {}),
  });

  /** What the Order Worker's transaction announces when it finished an order: the order, its stock change and the task. */
  const orderFinished = () => [
    { type: 'order.created', orderId: ORDER_ID, channelId: 'channel-website', status: 'Reserved' },
    { type: 'inventory.changed', productIds: [TEA_ID] },
    taskChanged('Succeeded', ORDER_ID),
  ];

  const indicator = () => screen.getByTestId('connection-indicator');

  /**
   * The page with a live hub connection, after the refetch every new connection causes. With `autoAccept: false` the
   * test decides about every later connection attempt (a lost connection stays lost until it accepts the next one).
   */
  async function renderLive({ autoAccept = true }: { autoAccept?: boolean } = {}) {
    const everything = serveEverything();
    const hub = fakeHub({ autoAccept });
    const app = await renderOrderEntry(staffUser, hub.create);
    if (!autoAccept) {
      await hub.latest().accept();
    }

    await waitFor(() => expect(indicator()).toHaveTextContent('Trực tiếp'));
    jump(FLUSH_WINDOW_MS);
    // The resync of the new connection waits for the page's first requests and then refetches: a test that counts
    // requests starts after it, however long the answers take.
    await settled(app.store);
    return { ...everything, ...app, hub };
  }

  /** An order submitted while live whose task waits in the queue. */
  async function submitWhileLive(options?: { autoAccept?: boolean }) {
    const live = await renderLive(options);
    live.backend.taskAnswer = () => task({ status: 'Pending' });
    const outcome = await submitOrder(live.user);
    await within(outcome).findByText('Đơn hàng đang chờ trong hàng đợi xử lý.');
    return { ...live, outcome };
  }

  /** The connection is lost; the reconnect attempt stays open. */
  async function loseConnection(hub: ReturnType<typeof fakeHub>) {
    await hub.latest().drop();
    await waitFor(() => expect(indicator()).toHaveTextContent('Đang kết nối lại'));
  }

  it('does not poll while live: two polling intervals later the task has been asked for once', async () => {
    const { backend } = await submitWhileLive();

    jump(2 * POLL_INTERVAL_MS + 300);
    await pause(200);
    expect(backend.taskRequests).toHaveLength(1);
  });

  it('asks for the task again only when a hint for it arrives; the order is loaded once', async () => {
    const { backend, hub, outcome } = await submitWhileLive();

    // The worker picked it up: the hint refetches the task.
    backend.taskAnswer = runningTask;
    await hub.latest().send([taskChanged('InProgress')]);
    expect(await within(outcome).findByText('Đơn hàng đang được xử lý.')).toBeInTheDocument();
    expect(backend.taskRequests).toHaveLength(2);

    // Finished. (Hints within the second of the previous refetch go out when that second is over.)
    backend.taskAnswer = succeededTask;
    await hub.latest().send(orderFinished());
    jump(FLUSH_WINDOW_MS);
    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();
    expect(outcome).toHaveTextContent('1.234.567 ₫');

    // Nothing more is asked afterwards; the order was loaded once although its tag was among the hints.
    jump(POLL_INTERVAL_MS + 700);
    await pause(200);
    expect(backend.taskRequests).toHaveLength(3);
    expect(backend.orderRequests).toEqual([ORDER_ID]);
  });

  it('refreshes the lists once when the order is finished, by the hints: the outcome invalidates nothing while live', async () => {
    const { backend, products, hub, store, user: appUser } = await renderLive();
    backend.taskAnswer = (requestNo) => (requestNo === 1 ? task({ status: 'Pending' }) : succeededTask());
    const counts = watchLists(store, products);
    await waitFor(() => expect(counts()).toEqual(LOADED_ONCE));

    const outcome = await submitOrder(appUser);
    await within(outcome).findByText('Đơn hàng đang chờ trong hàng đợi xử lý.');

    await hub.latest().send(orderFinished());
    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();
    await waitFor(() => expect(counts()).toEqual(LOADED_TWICE));

    jump(POLL_INTERVAL_MS + 700);
    await pause(200);
    expect(counts()).toEqual(LOADED_TWICE);

    await settled();
  });

  it('leaves the lists alone when it sees the task finished while live: only hints refresh them', async () => {
    const { backend, products, hub, store, user: appUser } = await renderLive();
    backend.taskAnswer = (requestNo) => (requestNo === 1 ? task({ status: 'Pending' }) : succeededTask());
    const counts = watchLists(store, products);
    await waitFor(() => expect(counts()).toEqual(LOADED_ONCE));

    const outcome = await submitOrder(appUser);
    expect(await within(outcome).findByText('Đơn hàng đang chờ trong hàng đợi xử lý.')).toBeInTheDocument();

    // A hint that names the task only (not what a worker sends; it shows what the outcome view does by itself).
    await hub.latest().send([taskChanged('Succeeded', ORDER_ID)]);
    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();

    jump(FLUSH_WINDOW_MS + 500);
    await pause(200);
    expect(counts()).toEqual(LOADED_ONCE);

    await settled();
  });

  it('refreshes the lists once when the connection was lost while waiting and the task is finished by polling', async () => {
    const { backend, products, hub, store, user: appUser } = await renderLive({ autoAccept: false });
    backend.taskAnswer = () => task({ status: 'Pending' });
    const counts = watchLists(store, products);
    await waitFor(() => expect(counts()).toEqual(LOADED_ONCE));

    const outcome = await submitOrder(appUser);
    await within(outcome).findByText('Đơn hàng đang chờ trong hàng đợi xử lý.');

    // Connection lost: no hint will come, the task is polled.
    await loseConnection(hub);
    backend.taskAnswer = succeededTask;
    jump(POLL_INTERVAL_MS + 100);
    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();

    // Not live when the task was seen finished: the outcome is the only one who can refresh the lists - once.
    await waitFor(() => expect(counts()).toEqual(LOADED_TWICE));
    jump(POLL_INTERVAL_MS + 700);
    await pause(200);
    expect(counts()).toEqual(LOADED_TWICE);

    await settled();
  });

  it('polls while the connection is lost, in the same outcome view', async () => {
    const { backend, hub, outcome } = await submitWhileLive({ autoAccept: false });
    const id = outcome.textContent?.match(/WEB-\d{8}-[A-Z0-9]{6}/)?.[0];

    await loseConnection(hub);
    backend.taskAnswer = runningTask;
    jump(POLL_INTERVAL_MS + 100);
    expect(await within(outcome).findByText('Đơn hàng đang được xử lý.')).toBeInTheDocument();
    jump(POLL_INTERVAL_MS + 100);
    await waitFor(() => expect(backend.taskRequests.length).toBeGreaterThanOrEqual(3));
    expect(screen.getByTestId('order-outcome-website')).toBe(outcome);
    expect(outcome).toHaveTextContent(id!);

    // The polling ends with the task: no request of this test is still on its way when the next one starts.
    backend.taskAnswer = succeededTask;
    jump(POLL_INTERVAL_MS + 100);
    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();
  });

  it('stops polling when the connection is back - one refetch for what was missed - and the push finishes the order in the same view', async () => {
    const { backend, hub, outcome } = await submitWhileLive({ autoAccept: false });
    const id = outcome.textContent?.match(/WEB-\d{8}-[A-Z0-9]{6}/)?.[0];
    await loseConnection(hub);
    backend.taskAnswer = runningTask;

    // Back: one refetch for what was missed, then no polling any more.
    await hub.latest().accept();
    await waitFor(() => expect(indicator()).toHaveTextContent('Trực tiếp'));
    expect(await within(outcome).findByText('Đơn hàng đang được xử lý.')).toBeInTheDocument();
    jump(FLUSH_WINDOW_MS + POLL_INTERVAL_MS);
    await pause(200);
    const afterReconnect = backend.taskRequests.length;
    jump(2 * POLL_INTERVAL_MS);
    await pause(200);
    expect(backend.taskRequests).toHaveLength(afterReconnect);
    expect(within(outcome).getByText('Đơn hàng đang được xử lý.')).toBeInTheDocument();

    // And the push finishes it.
    backend.taskAnswer = succeededTask;
    await hub.latest().send([taskChanged('Succeeded', ORDER_ID)]);
    expect(await within(outcome).findByText('Đã giữ hàng cho đơn hàng.')).toBeInTheDocument();
    expect(screen.getByTestId('order-outcome-website')).toBe(outcome);
    expect(outcome).toHaveTextContent(id!);
  });
});

/** The 60 seconds are jumped over, as in "polling window". */
describe('order entry: waiting window in push mode', () => {
  useJumpableClock();

  it('says "still processing" after 60 seconds without having asked again, and "Check again" asks once', async () => {
    const { backend } = serveEverything();
    backend.taskAnswer = () => task({ status: 'Pending' });
    const hub = fakeHub({ autoAccept: true });
    const { user: appUser } = await renderOrderEntry(staffUser, hub.create);
    await waitFor(() => expect(screen.getByTestId('connection-indicator')).toHaveTextContent('Trực tiếp'));
    // The refetch every new connection causes is over before the order is submitted.
    jump(FLUSH_WINDOW_MS);
    const stillProcessing = 'Đơn hàng vẫn đang được xử lý; đã dừng tự động kiểm tra.';

    const outcome = await submitOrder(appUser);
    await within(outcome).findByText('Đơn hàng đang chờ trong hàng đợi xử lý.');
    expect(backend.taskRequests).toHaveLength(1);

    jump(POLL_WINDOW_MS);
    expect(await within(outcome).findByText(stillProcessing)).toBeInTheDocument();
    await pause(200);
    // A timer, no requests.
    expect(backend.taskRequests).toHaveLength(1);

    await appUser.click(await within(outcome).findByRole('button', { name: 'Kiểm tra lại' }));
    await waitFor(() => expect(backend.taskRequests).toHaveLength(2));
    expect(within(outcome).queryByText(stillProcessing)).not.toBeInTheDocument();

    // Another window, again without requests.
    await pause(200);
    jump(POLL_WINDOW_MS);
    expect(await within(outcome).findByText(stillProcessing)).toBeInTheDocument();
    await pause(200);
    expect(backend.taskRequests).toHaveLength(2);
  });
});

describe('order entry: language switch', () => {
  it('changes text only: a form with its lines and the external order ids stay', async () => {
    const { otherRequests } = serveEverything();
    const { user: appUser, location } = await renderOrderEntry();
    const ids = (['digital-platform', 'website', 'pos'] as const).map((slug) => externalOrderId(slug).value);
    await fillOrder(appUser, 'website');
    await addCoffeeLine(appUser, 'website');
    await appUser.click(panelOf('website').getByRole('button', { name: /Thêm dòng/ }));

    await expectLanguageSwitchChangesTextOnly(appUser, location, {
      vietnamese: [
        'Nhập đơn hàng',
        'Mã đơn hàng bên ngoài',
        'Các dòng hàng',
        'Thêm dòng',
        'Tạo đơn hàng',
        'POS (tại quầy)',
      ],
      english: ['Order entry', 'External order ID', 'Lines', 'Add line', 'Create order', 'POS'],
      requestCount: otherRequests,
    });

    expect((['digital-platform', 'website', 'pos'] as const).map((slug) => externalOrderId(slug).value)).toEqual(ids);
    expect(pickerTexts('website')).toEqual(['Nguyễn Văn An', 'TEA-01 — Trà xanh', 'COF-01 — Cà phê', 'Chọn sản phẩm']);
    expect(quantityOfLine('website', 1)).toHaveValue('3');
    expect(quantityOfLine('website', 2)).toHaveValue('2');
    expect(location()).toBe('/order-entry');
  });

  it('changes text only while an outcome is being polled: no extra request, and the polling is not restarted', async () => {
    const { backend, otherRequests } = serveEverything();
    backend.taskAnswer = () =>
      task({ status: 'InProgress', attempts: [attempt({ status: 'Running', finishedAt: null })] });
    const { user: appUser, location } = await renderOrderEntry();
    const outcome = await submitOrder(appUser);
    await within(outcome).findByText('Đơn hàng đang được xử lý.');
    const pollsBefore = backend.taskRequests.length;

    await expectLanguageSwitchChangesTextOnly(appUser, location, {
      vietnamese: ['Đơn hàng đang được xử lý.', 'Trạng thái xử lý', 'Đang xử lý', 'Đơn hàng mới'],
      english: ['The order is being processed.', 'Processing status', 'In progress', 'New order'],
      requestCount: otherRequests,
    });

    // The same outcome view (not a new one), still polling at the same pace: a restart would show as a request
    // right after a switch, i.e. as two requests less than one interval apart.
    expect(screen.getByTestId('order-outcome-website')).toBe(outcome);
    await waitFor(() => expect(backend.taskRequests.length).toBeGreaterThan(pollsBefore + 1));
    const gaps = backend.taskRequests.slice(1).map((at, index) => at - backend.taskRequests[index]!);
    expect(Math.min(...gaps)).toBeGreaterThanOrEqual(POLL_INTERVAL_MS - 100);

    await settled();
  });
});
