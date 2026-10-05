import { act, screen, waitFor, within } from '@testing-library/react';
import dayjs from 'dayjs';
import { HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import type { HubFactory } from '@/features/realtime/hub';
import { FLUSH_WINDOW_MS } from '@/features/realtime/hintTags';
import { AN_ID, anCustomer, binhCustomer, serveCustomerCatalog } from '@/test/catalog';
import { serveSummary, summary } from '@/test/dashboard';
import { fakeHub } from '@/test/hub';
import { chooseOption, columnTexts, rowOf, settled, switchLanguage } from '@/test/interactions';
import { serveChannels, TASK_ID } from '@/test/orders';
import { failedSyncDetail, serveTask, serveTaskList, taskSummary } from '@/test/processing';
import { renderApp } from '@/test/renderApp';
import {
  downloadLink,
  expiredExport,
  FAILED_EXPORT_ID,
  failedExport,
  INVENTORY_EXPORT_ID,
  inventoryExport,
  ORDERS_EXPORT_ID,
  ordersExport,
  PENDING_EXPORT_ID,
  pendingExport,
  reportExport,
  serveDownloads,
  serveExportRequests,
  serveExports,
} from '@/test/reports';
import { adminUser, managerUser, problem, sessionOf, staffUser } from '@/test/server';
import { exportRejectedEvent, exportRequestedEvent, serveSystemEvents } from '@/test/systemEvents';
import { EXPORTS_POLL_INTERVAL_MS } from './components/ExportsTable';
import { browserTimeZone, defaultRange, MAX_EXPORT_RANGE_DAYS, rangeDays } from './exportRequest';
import { exportPrefillState, REPORTS_PATH, type ExportPrefill } from './paths';
import type { ReportExport } from './reportsApi';

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
const DAY = 'DD/MM/YYYY';
const ACCEPTED = 'Đã yêu cầu xuất tệp - tệp sẽ xuất hiện bên dưới khi sẵn sàng.';

/** Everything the Reports page talks to: its list, the request and download endpoints, channels and customers. */
function serveReports(initial: ReportExport[] = []) {
  return {
    exports: serveExports(initial),
    sent: serveExportRequests(),
    downloads: serveDownloads(),
    channels: serveChannels(),
    customers: serveCustomerCatalog([anCustomer, binhCustomer]),
  };
}

interface RenderOptions {
  user?: AuthUser;
  route?: string;
  /** Arrive like a click on "Export to Excel" of a list: with that list's filters in the location state. */
  prefill?: ExportPrefill;
  createHub?: HubFactory;
}

async function renderReports({ user = managerUser, route = REPORTS_PATH, prefill, createHub }: RenderOptions = {}) {
  const app = renderApp({ route: prefill ? '/no-such-page' : route, session: sessionOf(user), createHub });
  if (prefill) {
    await screen.findByText('Không tìm thấy trang');
    await act(() => app.router.navigate(REPORTS_PATH, { state: exportPrefillState(prefill) }));
  }

  await screen.findByRole('heading', { name: 'Báo cáo & xuất Excel' });
  return app;
}

const ordersForm = () => within(screen.getByTestId('orders-export-form'));
const inventoryForm = () => within(screen.getByTestId('inventory-export-form'));
const rangeValues = () =>
  [ordersForm().getByPlaceholderText('Từ ngày'), ordersForm().getByPlaceholderText('Đến ngày')].map(
    (input) => (input as HTMLInputElement).value,
  );
const submitButton = () => screen.getByRole('button', { name: /Yêu cầu xuất/ });
/** The message above the forms (an error that belongs to no field). */
const formAlert = () => document.querySelector('.ant-card .ant-alert-error');
/** The form item of a field: its control and what is said under it. */
const fieldOf = (label: string) =>
  screen.getByText(label, { selector: 'label' }).closest<HTMLElement>('.ant-form-item')!;
/** What is said under a field (a rule of the form, or the API's message for that field). */
const fieldError = (label: string) => fieldOf(label).querySelector('.ant-form-item-explain-error');
const dataRows = () => document.querySelectorAll('tbody tr.ant-table-row');

describe('reports: who may open the page', () => {
  it.each([
    ['Manager', managerUser],
    ['Admin', adminUser],
  ] as const)('%s has the menu item and the page: the request form and the own exports', async (_role, user) => {
    const { exports } = serveReports([ordersExport]);
    await renderReports({ user });

    expect(screen.getByRole('link', { name: 'Báo cáo' })).toHaveAttribute('href', REPORTS_PATH);
    expect(screen.getByText('Yêu cầu xuất tệp')).toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'Tệp xuất của tôi' })).toBeInTheDocument();
    await waitFor(() => expect(dataRows()).toHaveLength(1));
    expect(exports.requests).toHaveLength(1);

    await settled();
  });

  it('is not for Staff: no menu item, and the address shows the no-access page without asking the API', async () => {
    const { exports, channels } = serveReports([ordersExport]);
    renderApp({ route: REPORTS_PATH, session: sessionOf(staffUser) });

    expect(await screen.findByText('Không có quyền truy cập')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Báo cáo' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Báo cáo & xuất Excel' })).not.toBeInTheDocument();
    await settled();
    expect(exports.requests).toHaveLength(0);
    expect(channels.count).toBe(0);
  });
});

describe('reports: request an orders export', () => {
  it('starts with the last 30 days up to today, no other filter, and says which time zone and language the file gets', async () => {
    serveReports();
    await renderReports();

    const [first, last] = defaultRange();
    expect(rangeDays([first, last])).toBe(30);
    expect(last.isSame(dayjs(), 'day')).toBe(true);
    expect(rangeValues()).toEqual([first.format(DAY), last.format(DAY)]);
    expect(ordersForm().getByLabelText('Tìm đơn hàng')).toHaveValue('');
    // Shown, not chosen.
    const note = ordersForm().getByText('Múi giờ trong tệp').closest('table, .ant-descriptions')!;
    expect(note).toHaveTextContent(browserTimeZone());
    expect(note).toHaveTextContent('Tiếng Việt - ngôn ngữ đang hiển thị');
    expect(submitButton()).toHaveStyle({ width: '176px' });

    await settled();
  });

  it('sends the filters, the first and the last instant of the local days and the time zone; the list is asked again and the form keeps its values', async () => {
    const { exports, sent } = serveReports();
    sent.state.onAccepted = (taskId) => {
      exports.state.items = [reportExport({ taskId, status: 'Pending', rowCount: null, sizeBytes: null })];
    };
    const { user, store } = await renderReports();
    await settled(store);
    expect(exports.requests).toHaveLength(1);

    await chooseOption(user, ordersForm().getByRole('combobox', { name: 'Kênh bán' }), 'POS');
    await user.type(ordersForm().getByLabelText('Tìm đơn hàng'), '  DP-2026 ');
    await user.click(submitButton());

    expect(await screen.findByText(ACCEPTED)).toBeInTheDocument();
    const [first, last] = defaultRange();
    expect(sent.requests).toEqual([
      {
        report: 'orders',
        language: 'vi',
        body: {
          channel: 'pos',
          search: 'DP-2026',
          from: first.startOf('day').format('YYYY-MM-DDTHH:mm:ssZ'),
          to: last.endOf('day').format('YYYY-MM-DDTHH:mm:ss.SSSZ'),
          timeZone: browserTimeZone(),
        },
      },
    ]);
    // The new export is in the table without a reload.
    await waitFor(() => expect(exports.requests).toHaveLength(2));
    await waitFor(() => expect(dataRows()).toHaveLength(1));
    // Nothing was reset.
    expect(ordersForm().getByLabelText('Tìm đơn hàng')).toHaveValue('  DP-2026 ');
    expect(rangeValues()).toEqual([first.format(DAY), last.format(DAY)]);

    await settled(store);
  });

  it('does not let the request be sent twice: the button is busy until the answer is there', async () => {
    const { sent } = serveReports();
    let release: () => void = () => {};
    sent.state.answer = () =>
      new Promise<Response>((resolve) => {
        release = () => resolve(HttpResponse.json({ taskId: PENDING_EXPORT_ID }, { status: 202 }));
      });
    const { user, store } = await renderReports();
    await settled(store);

    await user.click(submitButton());
    await waitFor(() => expect(sent.requests).toHaveLength(1));
    expect(submitButton()).toBeDisabled();

    release();
    expect(await screen.findByText(ACCEPTED)).toBeInTheDocument();
    await waitFor(() => expect(submitButton()).toBeEnabled());
    expect(sent.requests).toHaveLength(1);
    await settled(store);
  });

  it(`blocks a range of ${MAX_EXPORT_RANGE_DAYS + 1} days before anything is sent, and lets ${MAX_EXPORT_RANGE_DAYS} days through`, async () => {
    const { sent } = serveReports();
    const last = dayjs('2026-10-05');
    const tooLong = { from: last.subtract(MAX_EXPORT_RANGE_DAYS, 'day').format('YYYY-MM-DD'), to: '2026-10-05' };
    const { user, store, unmount } = await renderReports({ prefill: { reportType: 'Orders', ...tooLong } });
    await settled(store);

    await user.click(submitButton());

    await waitFor(() =>
      expect(fieldError('Đơn tạo trong khoảng')).toHaveTextContent(
        `Khoảng ngày không được dài hơn ${MAX_EXPORT_RANGE_DAYS} ngày.`,
      ),
    );
    await pause(100);
    expect(sent.requests).toHaveLength(0);
    unmount();

    // One day less is the longest range the form lets through; the API decides about it.
    const longest = {
      from: last.subtract(MAX_EXPORT_RANGE_DAYS - 1, 'day').format('YYYY-MM-DD'),
      to: '2026-10-05',
    };
    const second = await renderReports({ prefill: { reportType: 'Orders', ...longest } });
    await second.user.click(submitButton());
    expect(await screen.findByText(ACCEPTED)).toBeInTheDocument();
    expect(sent.requests).toHaveLength(1);
    await settled(second.store);
  });

  it.each([
    ['vi', "'Đến ngày' không được trước 'Từ ngày'.", "'Tìm kiếm' không được dài quá 100 ký tự."],
    ['en', "'To date' must not be earlier than 'From date'.", "'Search' must not be longer than 100 characters."],
  ] as const)(
    'shows what the API says about a field on that field, in the language of the request (%s)',
    async (language, rangeMessage, searchMessage) => {
      const { sent } = serveReports();
      sent.state.answer = ({ language: asked }) =>
        problem(400, {
          errorCode: 'validation_failed',
          errors: asked === language ? { to: [rangeMessage], search: [searchMessage] } : { to: ['wrong language'] },
        });
      const { user, store } = await renderReports();
      if (language === 'en') {
        await switchLanguage(user, 'EN');
        await screen.findByRole('heading', { name: 'Reports & Excel export' });
      }

      await user.click(screen.getByRole('button', { name: /Yêu cầu xuất|Request export/ }));

      // The two ends of the range are one field here.
      const rangeLabel = language === 'vi' ? 'Đơn tạo trong khoảng' : 'Orders created';
      const searchLabel = language === 'vi' ? 'Tìm đơn hàng' : 'Search orders';
      await waitFor(() => expect(fieldError(rangeLabel)).toHaveTextContent(rangeMessage));
      expect(fieldError(searchLabel)).toHaveTextContent(searchMessage);
      expect(formAlert()).toBeNull();
      expect(sent.requests[0]?.language).toBe(language);
      await settled(store);
    },
  );

  it.each([
    ['export_too_large', 400, 'Tệp xuất sẽ có nhiều dòng hơn mức cho phép. Hãy thu hẹp bộ lọc rồi thử lại.'],
    [
      'too_many_exports',
      409,
      'Bạn đang có số lần xuất tệp đang chờ hoặc đang chạy ở mức tối đa. Hãy đợi một lần xuất hoàn tất.',
    ],
  ] as const)('says %s above the form: it belongs to no field', async (errorCode, status, message) => {
    const { sent, exports } = serveReports();
    sent.state.answer = () => problem(status, { errorCode, detail: 'The server text is not shown.' });
    const { user, store } = await renderReports();
    await settled(store);

    await user.click(submitButton());

    await waitFor(() => expect(formAlert()).toHaveTextContent(message));
    expect(screen.queryByText(ACCEPTED)).not.toBeInTheDocument();
    expect(document.querySelectorAll('.ant-form-item-explain-error')).toHaveLength(0);
    // Nothing was accepted: the list is not asked again.
    await settled(store);
    expect(exports.requests).toHaveLength(1);

    // The next request starts clean.
    sent.state.answer = undefined;
    await user.click(submitButton());
    expect(await screen.findByText(ACCEPTED)).toBeInTheDocument();
    expect(formAlert()).toBeNull();
    await settled(store);
  });

  it('shows a validation error of something that is no field of the form (the time zone) above the form', async () => {
    const { sent } = serveReports();
    const message = "'Múi giờ' phải là tên một múi giờ, ví dụ Asia/Ho_Chi_Minh.";
    sent.state.answer = () => problem(400, { errorCode: 'validation_failed', errors: { timeZone: [message] } });
    const { user, store } = await renderReports();

    await user.click(submitButton());

    await waitFor(() => expect(formAlert()).toHaveTextContent(message));
    await settled(store);
  });
});

describe('reports: request an inventory export', () => {
  it('sends the search text, "low stock only" and the time zone', async () => {
    const { sent, exports } = serveReports();
    const { user, store } = await renderReports();
    await settled(store);

    await user.click(screen.getByRole('radio', { name: 'Tồn kho' }));
    await user.type(inventoryForm().getByLabelText('Tìm sản phẩm'), 'TEA');
    await user.click(inventoryForm().getByRole('switch'));
    await user.click(submitButton());

    expect(await screen.findByText(ACCEPTED)).toBeInTheDocument();
    expect(sent.requests).toEqual([
      { report: 'inventory', language: 'vi', body: { search: 'TEA', lowStockOnly: true, timeZone: browserTimeZone() } },
    ]);
    await waitFor(() => expect(exports.requests).toHaveLength(2));
    await settled(store);
  });

  it('keeps what was entered in one form while the other is looked at', async () => {
    const { sent } = serveReports();
    const { user, store } = await renderReports();

    await user.type(ordersForm().getByLabelText('Tìm đơn hàng'), 'WEB-1');
    await user.click(screen.getByRole('radio', { name: 'Tồn kho' }));
    expect(screen.getByTestId('orders-export-form')).not.toBeVisible();
    await user.click(submitButton());
    expect(await screen.findByText(ACCEPTED)).toBeInTheDocument();
    // No filter: everything, and "low stock only" is sent as what it is.
    expect(sent.requests[0]).toMatchObject({ report: 'inventory', body: { lowStockOnly: false } });
    expect(sent.requests[0]?.body).not.toHaveProperty('search');

    await user.click(screen.getByRole('radio', { name: 'Đơn hàng' }));
    expect(screen.getByTestId('orders-export-form')).toBeVisible();
    expect(ordersForm().getByLabelText('Tìm đơn hàng')).toHaveValue('WEB-1');
    await settled(store);
  });
});

describe('reports: opened from a list ("Export to Excel")', () => {
  it('fills the orders form with the filters of the list and its range; nothing is requested before the user submits', async () => {
    const { sent, customers } = serveReports();
    const { user, store } = await renderReports({
      prefill: {
        reportType: 'Orders',
        channel: 'pos',
        status: 'Reserved',
        customerId: AN_ID,
        search: 'DP-2026',
        from: '2026-09-01',
        to: '2026-09-30',
      },
    });

    expect(rangeValues()).toEqual(['01/09/2026', '30/09/2026']);
    expect(ordersForm().getByLabelText('Tìm đơn hàng')).toHaveValue('DP-2026');
    // The names of what the list had chosen: the channel from the channel list, the customer by its id.
    expect(await ordersForm().findByText('POS')).toBeInTheDocument();
    expect(ordersForm().getByText('Đã giữ hàng')).toBeInTheDocument();
    expect(await ordersForm().findByText('Nguyễn Văn An')).toBeInTheDocument();
    expect(customers.detailRequests).toEqual([AN_ID]);
    await settled(store);
    expect(sent.requests).toHaveLength(0);

    await user.click(submitButton());

    expect(await screen.findByText(ACCEPTED)).toBeInTheDocument();
    expect(sent.requests[0]?.body).toEqual({
      channel: 'pos',
      status: 'Reserved',
      customerId: AN_ID,
      search: 'DP-2026',
      from: dayjs('2026-09-01').startOf('day').format('YYYY-MM-DDTHH:mm:ssZ'),
      to: dayjs('2026-09-30').endOf('day').format('YYYY-MM-DDTHH:mm:ss.SSSZ'),
      timeZone: browserTimeZone(),
    });
    await settled(store);
  });

  it('uses the default range when the list had none, and ignores values that are not what they should be', async () => {
    serveReports();
    await renderReports({
      prefill: { reportType: 'Orders', status: 'NoSuchStatus', customerId: 'not-an-id', from: '2026-13-45' },
    });

    const [first, last] = defaultRange();
    expect(rangeValues()).toEqual([first.format(DAY), last.format(DAY)]);
    expect(ordersForm().queryByText('NoSuchStatus')).not.toBeInTheDocument();
    await settled();
  });

  it('opens the inventory form with the filters of the inventory list', async () => {
    const { sent } = serveReports();
    const { user, store } = await renderReports({
      prefill: { reportType: 'Inventory', search: 'TEA', lowStockOnly: true },
    });

    expect(screen.getByRole('radio', { name: 'Tồn kho' })).toBeChecked();
    expect(screen.getByTestId('inventory-export-form')).toBeVisible();
    expect(inventoryForm().getByLabelText('Tìm sản phẩm')).toHaveValue('TEA');
    expect(inventoryForm().getByRole('switch')).toBeChecked();
    await settled(store);
    expect(sent.requests).toHaveLength(0);

    await user.click(submitButton());
    expect(await screen.findByText(ACCEPTED)).toBeInTheDocument();
    expect(sent.requests[0]?.body).toEqual({ search: 'TEA', lowStockOnly: true, timeZone: browserTimeZone() });
    await settled(store);
  });
});

describe('reports: my exports', () => {
  it('shows per export when it was asked for, what for, its status, rows, size and until when it is kept', async () => {
    const { customers } = serveReports([inventoryExport, ordersExport]);
    await renderReports();
    await waitFor(() => expect(dataRows()).toHaveLength(2));

    expect([...document.querySelectorAll('thead th')].map((th) => th.textContent)).toEqual([
      'Yêu cầu lúc',
      'Báo cáo',
      'Bộ lọc',
      'Trạng thái',
      'Số dòng',
      'Dung lượng',
      'Lưu đến',
      'Thao tác',
    ]);
    expect(columnTexts('Yêu cầu lúc')).toEqual(['05/10/2026 09:00', '05/10/2026 10:30']);
    expect(columnTexts('Báo cáo')).toEqual(['Tồn kho', 'Đơn hàng']);
    expect(columnTexts('Trạng thái')).toEqual(['Thành công', 'Thành công']);
    expect(columnTexts('Số dòng')).toEqual(['20', '1.234']);
    expect(columnTexts('Dung lượng')).toEqual(['5,5 MB', '4,2 KB']);
    expect(columnTexts('Lưu đến')).toEqual(['12/10/2026 09:00', '12/10/2026 10:30']);
    // The filters as one line: the range as local days, names instead of the slug and the id.
    await waitFor(() =>
      expect(columnTexts('Bộ lọc')).toEqual([
        'Chỉ hàng sắp hết',
        '01/09/2026 – 30/09/2026 · Kênh: POS · Trạng thái: Đã giữ hàng · Khách hàng: Nguyễn Văn An · Tìm: DP-2026',
      ]),
    );
    expect(customers.detailRequests).toEqual([AN_ID]);
    await settled();
  });

  it('asks for a customer once, however many exports name it; an export without filters says so', async () => {
    const second = reportExport({
      taskId: PENDING_EXPORT_ID,
      parameters: { ...ordersExport.parameters, search: null },
    });
    const everything = reportExport({
      taskId: INVENTORY_EXPORT_ID,
      reportType: 'Inventory',
      parameters: { search: null, lowStockOnly: false, timeZone: 'UTC', language: 'en' },
    });
    const { customers, store } = { ...serveReports([ordersExport, second, everything]), ...(await renderReports()) };
    await waitFor(() => expect(dataRows()).toHaveLength(3));

    await waitFor(() => expect(columnTexts('Bộ lọc')[1]).toContain('Khách hàng: Nguyễn Văn An'));
    expect(columnTexts('Bộ lọc')[2]).toBe('Tất cả sản phẩm');
    await settled(store);
    expect(customers.detailRequests).toEqual([AN_ID]);
  });

  it('restores page, page size, direction and the report filter from the address, and writes a changed filter there', async () => {
    const { exports } = serveReports([inventoryExport]);
    exports.state.totalCount = 25;
    const { user, location, store } = await renderReports({
      route: `${REPORTS_PATH}?page=2&pageSize=10&sortDir=asc&reportType=Inventory`,
    });
    await waitFor(() => expect(dataRows()).toHaveLength(1));

    expect(exports.requests[0]).toEqual({
      page: '2',
      pageSize: '10',
      sortBy: 'createdAt',
      sortDir: 'asc',
      reportType: 'Inventory',
    });
    expect(document.querySelector('.ant-pagination-item-active')).toHaveTextContent('2');
    expect(document.querySelector('thead th[aria-sort]')).toHaveAttribute('aria-sort', 'ascending');

    // Another report: the first page of it.
    await chooseOption(user, screen.getByRole('combobox', { name: 'Báo cáo' }), 'Đơn hàng');
    await waitFor(() => expect(location()).toBe(`${REPORTS_PATH}?pageSize=10&sortDir=asc&reportType=Orders`));
    await waitFor(() =>
      expect(exports.requests.at(-1)).toEqual({
        page: '1',
        pageSize: '10',
        sortBy: 'createdAt',
        sortDir: 'asc',
        reportType: 'Orders',
      }),
    );
    await settled(store);
  });

  it('asks the default list with nothing but the page: newest first is the order of the API', async () => {
    const { exports } = serveReports([ordersExport]);
    const { location, store } = await renderReports({ route: `${REPORTS_PATH}?reportType=Payments&sortDir=up` });
    await waitFor(() => expect(dataRows()).toHaveLength(1));

    // Values in the address that are not valid are never sent.
    expect(exports.requests[0]).toEqual({ page: '1', pageSize: '20' });
    expect(document.querySelector('thead th[aria-sort]')).toHaveAttribute('aria-sort', 'descending');
    expect(location()).toBe(`${REPORTS_PATH}?reportType=Payments&sortDir=up`);
    await settled(store);
  });

  it('offers nothing for an expired or unfinished export; a failed one names its error and leads to its task', async () => {
    const unknownFailure = reportExport({ ...failedExport, taskId: TASK_ID, errorCode: 'something_new' });
    serveReports([pendingExport, failedExport, unknownFailure, expiredExport]);
    serveTask(failedSyncDetail({ taskId: FAILED_EXPORT_ID }));
    const { user, location } = await renderReports();
    await waitFor(() => expect(dataRows()).toHaveLength(4));
    const [pending, failed, unknown, expired] = [...dataRows()] as HTMLElement[];

    expect(columnTexts('Trạng thái')).toEqual(['Đang chờ', 'Thất bại', 'Thất bại', 'Đã hết hạn']);
    // Grey: nothing is wrong with an expired export.
    expect(within(expired!).getByText('Đã hết hạn').closest('.ant-tag')).not.toHaveClass(
      'ant-tag-error',
      'ant-tag-success',
    );
    expect(screen.queryByRole('button', { name: /Tải về/ })).not.toBeInTheDocument();
    for (const row of [pending!, expired!]) {
      expect(within(row).queryAllByRole('button')).toHaveLength(0);
    }

    // The label of a known code, an unknown code as it is.
    expect(within(failed!).getByText('Không kết nối được kho tệp')).toBeInTheDocument();
    expect(within(unknown!).getByText('something_new')).toBeInTheDocument();
    // No size, no rows where there is no file.
    expect(columnTexts('Số dòng').slice(0, 3)).toEqual(['', '', '']);

    // The task is where the attempts and the retry are.
    await user.click(within(failed!).getByRole('button', { name: 'Mở tác vụ' }));
    await waitFor(() => expect(location()).toBe(`/processing/tasks/${FAILED_EXPORT_ID}`));
    await screen.findByText('channel_rejected');
    await settled();
  });

  it('keeps a row inside a 1280 px window: the columns are as wide as the content area, the actions stay in view', async () => {
    serveReports([ordersExport]);
    await renderReports();
    await waitFor(() => expect(dataRows()).toHaveLength(1));

    const widths = [...document.querySelectorAll('table colgroup col')].map((col) =>
      Number.parseInt((col as HTMLElement).style.width, 10),
    );
    // 992 px of content area in a 1280 px window, minus a vertical scrollbar.
    expect(widths.reduce((sum, width) => sum + width, 0)).toBe(976);
    expect(document.querySelector('thead th:last-child')?.className).toMatch(/ant-table-cell-fix/);
    await settled();
  });
});

describe('reports: live status', () => {
  it('is refetched by a hint about an export task, not by a hint about another task, and again after a reconnect', async () => {
    const { exports } = serveReports([pendingExport]);
    const hub = fakeHub({ autoAccept: true });
    const { store } = await renderReports({ createHub: hub.create });
    await screen.findByText('Đang chờ');
    await waitFor(() => expect(screen.getByTestId('connection-indicator')).toHaveTextContent('Trực tiếp'));
    // The page's own request and the one every new connection causes.
    await pause(FLUSH_WINDOW_MS + 300);
    await settled(store);
    const loaded = exports.requests.length;

    // The worker finished the export: the row changes without a reload.
    exports.state.items = [{ ...pendingExport, status: 'Succeeded', rowCount: 3, sizeBytes: 2048 }];
    await hub
      .latest()
      .send([
        { type: 'processing.task.changed', taskId: PENDING_EXPORT_ID, taskType: 'ReportExport', status: 'Succeeded' },
      ]);
    expect(await screen.findByRole('button', { name: /Tải về/ })).toBeInTheDocument();
    expect(exports.requests).toHaveLength(loaded + 1);

    // Tasks of other types, and other events, leave the exports alone.
    await pause(FLUSH_WINDOW_MS + 300);
    await hub.latest().send([
      { type: 'processing.task.changed', taskId: TASK_ID, taskType: 'StockSync', status: 'Failed' },
      { type: 'processing.task.changed', taskId: ORDERS_EXPORT_ID, taskType: 'OrderProcessing', orderId: 'o-1' },
      { type: 'inventory.changed', productIds: ['p1'] },
      { type: 'alert.changed', alertId: 'a-1' },
    ]);
    await pause(FLUSH_WINDOW_MS + 300);
    await settled(store);
    expect(exports.requests).toHaveLength(loaded + 1);

    // Hints may have been missed while the connection was gone: everything realtime keeps fresh is asked again.
    await hub.latest().send({ events: [], resync: true });
    await waitFor(() => expect(exports.requests).toHaveLength(loaded + 2));
    await settled(store);
  });

  describe('without a live connection', () => {
    beforeEach(() => {
      vi.useFakeTimers({
        shouldAdvanceTime: true,
        toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'],
      });
    });

    afterEach(() => {
      vi.useRealTimers();
    });

    const jump = (ms: number) =>
      act(() => {
        vi.advanceTimersByTime(ms);
      });

    it('asks every 5 seconds while an export on the page is not finished, and stops when all are', async () => {
      const { exports } = serveReports([pendingExport, ordersExport]);
      const { store } = await renderReports();
      await screen.findByText('Đang chờ');
      await settled(store);
      expect(exports.requests).toHaveLength(1);

      jump(EXPORTS_POLL_INTERVAL_MS - 500);
      await settled(store);
      expect(exports.requests).toHaveLength(1);

      exports.state.items = [{ ...pendingExport, status: 'InProgress' }, ordersExport];
      jump(600);
      expect(await screen.findByText('Đang xử lý')).toBeInTheDocument();
      expect(exports.requests).toHaveLength(2);

      exports.state.items = [{ ...pendingExport, status: 'Succeeded', rowCount: 3, sizeBytes: 2048 }, ordersExport];
      jump(EXPORTS_POLL_INTERVAL_MS + 100);
      await waitFor(() => expect(screen.getAllByRole('button', { name: /Tải về/ })).toHaveLength(2));
      expect(exports.requests).toHaveLength(3);

      // Nothing is left to wait for.
      jump(3 * EXPORTS_POLL_INTERVAL_MS);
      await settled(store);
      expect(exports.requests).toHaveLength(3);
    });

    it('never polls a page whose exports are all finished', async () => {
      const { exports } = serveReports([ordersExport, failedExport, expiredExport]);
      const { store } = await renderReports();
      await waitFor(() => expect(dataRows()).toHaveLength(3));
      await settled(store);

      jump(3 * EXPORTS_POLL_INTERVAL_MS);
      await settled(store);
      expect(exports.requests).toHaveLength(1);
    });
  });

  it('does not poll while the connection is live: the hints do the work', async () => {
    vi.useFakeTimers({
      shouldAdvanceTime: true,
      toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'],
    });
    try {
      const { exports } = serveReports([pendingExport]);
      const hub = fakeHub({ autoAccept: true });
      const { store } = await renderReports({ createHub: hub.create });
      await screen.findByText('Đang chờ');
      await waitFor(() => expect(screen.getByTestId('connection-indicator')).toHaveTextContent('Trực tiếp'));
      act(() => {
        vi.advanceTimersByTime(FLUSH_WINDOW_MS + 300);
      });
      await settled(store);
      const loaded = exports.requests.length;

      act(() => {
        vi.advanceTimersByTime(3 * EXPORTS_POLL_INTERVAL_MS);
      });
      await settled(store);
      expect(exports.requests).toHaveLength(loaded);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('reports: download', () => {
  /** What the page did with the link: every anchor that was clicked, as it was at that moment. */
  function watchAnchors() {
    const followed: { href: string; rel: string; target: string; inDocument: boolean }[] = [];
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      followed.push({ href: this.href, rel: this.rel, target: this.target, inDocument: this.isConnected });
    });
    return { followed, restore: () => click.mockRestore() };
  }

  it('asks for a link and follows it at once through an anchor; the link stays nowhere', async () => {
    const { downloads } = serveReports([ordersExport, inventoryExport]);
    const anchors = watchAnchors();
    try {
      const { user, store, location } = await renderReports();
      await waitFor(() => expect(dataRows()).toHaveLength(2));
      await settled(store);

      await user.click(within(rowOf('05/10/2026 10:30')).getByRole('button', { name: /Tải về/ }));

      const link = downloadLink(ORDERS_EXPORT_ID).url;
      await waitFor(() => expect(anchors.followed).toHaveLength(1));
      // No new window, and the storage does not learn where the link came from.
      expect(anchors.followed[0]).toEqual({ href: link, rel: 'noreferrer', target: '', inDocument: true });
      expect(downloads.requests).toEqual([
        { taskId: ORDERS_EXPORT_ID, authorization: `Bearer ${sessionOf(managerUser).accessToken}` },
      ]);
      await settled(store);

      // The anchor is gone, the page is where it was, and the link is in no store, address or message.
      expect(document.querySelector(`a[href="${link}"]`)).toBeNull();
      expect(location()).toBe(REPORTS_PATH);
      expect(JSON.stringify(store.getState())).not.toContain('X-Amz-Signature');
      expect(document.body.textContent).not.toContain('X-Amz-Signature');
      // The other row can be downloaded again right away.
      expect(within(rowOf('05/10/2026 09:00')).getByRole('button', { name: /Tải về/ })).toBeEnabled();
    } finally {
      anchors.restore();
    }
  });

  it.each([
    ['export_expired', 409, 'Lần xuất này đã hết hạn và tệp đã bị xoá. Hãy yêu cầu xuất lại.'],
    ['export_not_ready', 409, 'Lần xuất này chưa có tệp để tải: nó chưa hoàn tất hoặc đã thất bại.'],
    ['export_not_found', 404, 'Không tìm thấy dữ liệu.'],
  ] as const)('says %s and shows the list as it is now; nothing is followed', async (errorCode, status, message) => {
    const { downloads, exports } = serveReports([ordersExport]);
    downloads.state.answer = () => problem(status, { errorCode, detail: 'The server text is not shown.' });
    const anchors = watchAnchors();
    try {
      const { user, store } = await renderReports();
      await waitFor(() => expect(dataRows()).toHaveLength(1));
      await settled(store);

      // What the API knows by now.
      exports.state.items = [{ ...ordersExport, status: 'Expired' }];
      await user.click(screen.getByRole('button', { name: /Tải về/ }));

      expect(await screen.findByText(message)).toBeInTheDocument();
      await waitFor(() => expect(exports.requests).toHaveLength(2));
      expect(await screen.findByText('Đã hết hạn')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: /Tải về/ })).not.toBeInTheDocument();
      expect(anchors.followed).toHaveLength(0);
      await settled(store);
    } finally {
      anchors.restore();
    }
  });
});

describe('reports: exports on the other pages', () => {
  const exportTask = taskSummary({ taskId: ORDERS_EXPORT_ID, taskType: 'ReportExport', orderId: null });

  it('names the task type on the Sync & Processing page, in the column and in the filter', async () => {
    const { requests } = serveTaskList([exportTask]);
    serveChannels();
    const { user, location } = renderApp({ route: '/processing', session: sessionOf(staffUser) });
    await waitFor(() => expect(dataRows()).toHaveLength(1));

    expect(columnTexts('Loại tác vụ')).toEqual(['Xuất Excel']);

    await chooseOption(user, screen.getByRole('combobox', { name: 'Loại tác vụ' }), 'Xuất Excel');
    await waitFor(() => expect(location()).toBe('/processing?taskType=ReportExport'));
    await waitFor(() => expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', taskType: 'ReportExport' }));
    await settled();
  });

  it.each([
    ['VI', 'Xuất Excel'],
    ['EN', 'Excel export'],
  ] as const)('names the task type in the processing card of the dashboard (%s)', async (language, label) => {
    const counts = { pending: 1, inProgress: 0, retryScheduled: 0, failed: 2, succeeded: 40 };
    serveSummary(summary({ processing: [{ taskType: 'ReportExport', ...counts }] }));
    const { user } = renderApp({ route: '/', session: sessionOf(staffUser) });
    const card = await screen.findByTestId('dashboard-processing');
    if (language === 'EN') {
      await switchLanguage(user, 'EN');
    }

    await waitFor(() =>
      expect([...card.querySelectorAll('tbody tr.ant-table-row')].map((row) => row.textContent)).toEqual([
        `${label}100240`,
      ]),
    );
    await settled();
  });

  it('says on the Logs page who asked for which export, and why a message of the export queue was rejected', async () => {
    serveSystemEvents([exportRejectedEvent, exportRequestedEvent]);
    const { user } = renderApp({ route: '/logs', session: sessionOf(managerUser) });
    await waitFor(() => expect(dataRows()).toHaveLength(2));

    expect(columnTexts('Sự kiện')).toEqual(['Thông điệp xuất tệp bị từ chối', 'Yêu cầu xuất tệp']);
    expect(columnTexts('Nội dung')).toEqual([
      'Thông điệp msg-41 bị từ chối: tác vụ không tồn tại',
      'Báo cáo: Đơn hàng',
    ]);
    // The requester is the actor of the event.
    expect(rowOf('Yêu cầu xuất tệp')).toHaveTextContent('manager@example.com');

    await switchLanguage(user, 'EN');
    await screen.findByText('Report: Orders');
    expect(columnTexts('Event')).toEqual(['Export message rejected', 'Export requested']);
    await settled();
  });
});
