import { screen, waitFor, within } from '@testing-library/react';
import { delay, http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import { productsApi } from '@/features/products/productsApi';
import { FLUSH_WINDOW_MS } from '@/features/realtime/hintTags';
import type { HubFactory } from '@/features/realtime/hub';
import { chooseProduct, coffeeProduct, COFFEE_ID, serveProductCatalog, teaProduct, TEA_ID } from '@/test/catalog';
import {
  COFFEE_MAPPING_ID,
  coffeeMapping,
  savedMapping,
  serveChannelDetail,
  SHOP_ID,
  shopChannel,
  TEA_MAPPING_ID,
  teaMapping,
  websiteChannel,
} from '@/test/channels';
import { WEBSITE_ID } from '@/test/dashboard';
import { fakeHub } from '@/test/hub';
import {
  chooseOption,
  columnTexts,
  expectLanguageSwitchChangesTextOnly,
  findDialog,
  rowOf,
  type AppUser,
} from '@/test/interactions';
import { serveChannels, task, TASK_ID } from '@/test/orders';
import { serveTask, SYNC_TASK_ID } from '@/test/processing';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
const WEBSITE_PATH = `/channels/${WEBSITE_ID}`;
const SHOP_PATH = `/channels/${SHOP_ID}`;

const NO_QUEUE_HINT =
  'Kênh này không có hàng đợi đồng bộ tồn kho: có thể gắn sản phẩm, nhưng tồn kho không bao giờ được gửi tới kênh.';
const INACTIVE_HINT = 'Kênh đang ngừng hoạt động: tồn kho không được gửi tới kênh cho đến khi kênh hoạt động lại.';
const TASKS_LINK = 'Tác vụ đồng bộ tồn kho của kênh này';
const NO_SYNC_REASONS =
  'Không bắt đầu đồng bộ khi kênh hoặc liên kết đang ngừng hoạt động, khi kênh không có đồng bộ tồn kho, hoặc khi không có thay đổi nào mà kênh cần biết.';

function renderDetail(route = WEBSITE_PATH, user: AuthUser = managerUser, createHub?: HubFactory) {
  return renderApp({ route, session: sessionOf(user), createHub });
}

const summaryOf = () => screen.getByTestId('channel-summary');

/** The header cell of the mapping table with exactly this text ("SKU" is also the start of "SKU trên kênh"). */
function header(text: string): HTMLElement {
  const found = [...document.querySelectorAll<HTMLElement>('thead th')].find((th) => th.textContent === text);
  if (!found) {
    throw new Error(`No column "${text}"`);
  }

  return found;
}

/** The stock-sync task a saved mapping started, as `GET /api/processing/tasks/{id}` returns it. */
const syncTask = (status: 'Pending' | 'Succeeded' | 'Failed') =>
  task({
    taskId: SYNC_TASK_ID,
    taskType: 'StockSync',
    status,
    channelId: WEBSITE_ID,
    productId: TEA_ID,
    productSku: 'TEA-01',
  });

async function openCreateDialog(user: AppUser) {
  await user.click(await screen.findByRole('button', { name: /Gắn sản phẩm/ }));
  return findDialog('Gắn sản phẩm vào kênh');
}

async function openEditDialog(user: AppUser, sku: string) {
  await screen.findByText(sku);
  await user.click(within(rowOf(sku)).getByRole('button', { name: 'Sửa' }));
  return findDialog('Sửa liên kết sản phẩm');
}

describe('channel detail', () => {
  it('shows the channel as the API sends it, with the link to its stock-sync tasks, under the Channels menu item', async () => {
    const { channelReads } = serveChannelDetail(websiteChannel, [teaMapping]);
    renderDetail(WEBSITE_PATH, staffUser);

    expect(await screen.findByRole('heading', { name: 'Website' })).toBeInTheDocument();
    expect(channelReads).toEqual([WEBSITE_ID]);

    const summary = summaryOf();
    expect(summary).toHaveTextContent('Tên kênhWebsite');
    expect(summary).toHaveTextContent('Slugwebsite');
    expect(summary).toHaveTextContent('Loại kênhWebsite');
    expect(within(summary).getByText('Đang hoạt động').closest('.ant-tag')).toHaveClass('ant-tag-success');
    // Icon and text, not colour alone.
    const synced = within(summary).getByText('Có đồng bộ').closest('.ant-tag');
    expect(synced?.querySelector('[role="img"]')).toBeInTheDocument();

    // Both filters: the task type and this channel.
    expect(within(summary).getByRole('link', { name: TASKS_LINK })).toHaveAttribute(
      'href',
      `/processing?taskType=StockSync&channelId=${WEBSITE_ID}`,
    );
    expect(screen.queryByText(NO_QUEUE_HINT)).not.toBeInTheDocument();
    expect(screen.queryByText(INACTIVE_HINT)).not.toBeInTheDocument();

    // No menu item of its own: the page belongs to "Channels".
    expect(document.querySelector('.ant-menu-item-selected')).toHaveTextContent('Kênh bán');
  });

  it('says that a channel without a stock-sync queue is never synced, and offers no task link', async () => {
    serveChannelDetail(shopChannel);
    renderDetail(SHOP_PATH, staffUser);

    expect(await screen.findByRole('heading', { name: 'Cửa hàng mới' })).toBeInTheDocument();
    expect(within(summaryOf()).getByText('Không đồng bộ')).toBeInTheDocument();
    expect(screen.getByText(NO_QUEUE_HINT).closest('.ant-alert')).toHaveClass('ant-alert-info');
    expect(screen.queryByRole('link', { name: TASKS_LINK })).not.toBeInTheDocument();
    // Active: nothing is said about an inactive channel.
    expect(screen.queryByText(INACTIVE_HINT)).not.toBeInTheDocument();
  });

  it('says that an inactive channel gets no stock; whether it is synced at all is still what the API says', async () => {
    serveChannelDetail({ ...websiteChannel, status: 'Inactive' });
    renderDetail(WEBSITE_PATH, staffUser);

    expect(await screen.findByText(INACTIVE_HINT)).toBeInTheDocument();
    expect(screen.getByText(INACTIVE_HINT).closest('.ant-alert')).toHaveClass('ant-alert-info');
    expect(within(summaryOf()).getByText('Ngừng hoạt động')).toBeInTheDocument();
    // `stockSyncEnabled` is true for it: the capability is shown, with its link, and no "never synced" text.
    expect(within(summaryOf()).getByText('Có đồng bộ')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: TASKS_LINK })).toBeInTheDocument();
    expect(screen.queryByText(NO_QUEUE_HINT)).not.toBeInTheDocument();
  });

  it('shows both hints for an inactive channel without a queue', async () => {
    serveChannelDetail({ ...shopChannel, status: 'Inactive' });
    renderDetail(SHOP_PATH, staffUser);

    expect(await screen.findByText(NO_QUEUE_HINT)).toBeInTheDocument();
    expect(screen.getByText(INACTIVE_HINT)).toBeInTheDocument();
  });

  it('shows the not-found view for an id that is no GUID, without asking the API', async () => {
    const { channelReads, listRequests } = serveChannelDetail(websiteChannel, [teaMapping]);
    serveChannels([websiteChannel]);
    const { user, location } = renderDetail('/channels/not-a-guid');

    expect(await screen.findByText('Không tìm thấy kênh bán')).toBeInTheDocument();
    await pause(100);
    expect(channelReads).toEqual([]);
    expect(listRequests).toEqual([]);

    await user.click(screen.getByRole('button', { name: 'Về danh sách' }));
    await waitFor(() => expect(location()).toBe('/channels'));
  });

  it('shows the not-found view when the API does not know the channel, and never asks for its mappings', async () => {
    const { channelReads, listRequests } = serveChannelDetail(null);
    renderDetail();

    expect(await screen.findByText('Không tìm thấy kênh bán')).toBeInTheDocument();
    expect(channelReads).toEqual([WEBSITE_ID]);
    await pause(100);
    expect(listRequests).toEqual([]);
    // A 404 of a page is not an unexpected error.
    expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();
  });

  it('is opened from the name in the channels list, and "Back" returns to the list', async () => {
    serveChannelDetail(websiteChannel, [teaMapping]);
    serveChannels([websiteChannel, shopChannel]);
    const { user, location } = renderApp({ route: '/channels', session: sessionOf(staffUser) });

    await user.click(await screen.findByRole('link', { name: 'Website' }));
    expect(await screen.findByTestId('channel-summary')).toBeInTheDocument();
    expect(location()).toBe(WEBSITE_PATH);

    // The list state of the detail (its search) is not carried to the channels list.
    await user.type(screen.getByRole('searchbox'), 'tea');
    await waitFor(() => expect(location()).toBe(`${WEBSITE_PATH}?search=tea`));

    await user.click(screen.getByRole('button', { name: /Quay lại/ }));
    await waitFor(() => expect(location()).toBe('/channels'));
    expect(await screen.findByRole('link', { name: 'Cửa hàng mới' })).toBeInTheDocument();
  });
});

describe('channel detail: mapping list', () => {
  it('lists the mappings as the API sends them, with a dash for what is not set', async () => {
    const { listRequests } = serveChannelDetail(websiteChannel, [teaMapping, coffeeMapping]);
    renderDetail(WEBSITE_PATH, staffUser);

    expect(await screen.findByText('TEA-01')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Sản phẩm trên kênh' })).toBeInTheDocument();
    expect(columnTexts('SKU')).toEqual(['TEA-01', 'COF-01']);
    expect(columnTexts('Tên sản phẩm')).toEqual(['Trà xanh', 'Cà phê']);
    expect(columnTexts('Mã trên kênh')).toEqual(['WEB-1001', 'WEB-1002']);
    expect(columnTexts('SKU trên kênh')).toEqual(['WEB-TEA', '—']);
    expect(columnTexts('Trạng thái')).toEqual(['Hoạt động', 'Ngừng']);
    expect(columnTexts('Ngày tạo')).toEqual(['02/10/2026 09:30', '01/10/2026 08:05']);
    expect(columnTexts('Cập nhật lúc')).toEqual(['02/10/2026 10:45', '—']);
    expect(within(rowOf('TEA-01')).getByText('Hoạt động').closest('.ant-tag')).toHaveClass('ant-tag-success');

    // Nothing in the address: the API's default order (SKU) is not asked for.
    expect(listRequests).toEqual([{ page: '1', pageSize: '20' }]);
  });

  it('fits the content area of a 1280 px window, with and without the action column', async () => {
    serveChannelDetail(websiteChannel, [teaMapping]);
    const widths = () =>
      [...document.querySelectorAll<HTMLElement>('.ant-table colgroup col')].map((col) =>
        Number.parseInt(col.style.width, 10),
      );
    const sum = (values: number[]) => values.reduce((total, width) => total + width, 0);

    // jsdom has no layout: the fixed column widths are what can be checked. 1280 px window - 240 px menu - 2 × 24 px
    // padding = 992 px.
    const manager = renderDetail(WEBSITE_PATH, managerUser);
    await screen.findByText('TEA-01');
    expect(widths()).toEqual([130, 150, 124, 124, 96, 132, 132, 88]);
    expect(sum(widths())).toBeLessThanOrEqual(992);
    manager.unmount();

    renderDetail(WEBSITE_PATH, staffUser);
    await screen.findByText('TEA-01');
    expect(widths()).toEqual([130, 150, 124, 124, 96, 132, 132]);
  });

  it('restores search, sort and paging from the address (reload) and writes changes back to it, starting on page 1 again', async () => {
    const { listRequests } = serveChannelDetail(websiteChannel, [teaMapping, coffeeMapping], 45);
    const { user, location } = renderDetail(`${WEBSITE_PATH}?page=2&sortBy=createdAt&sortDir=desc&search=web`);

    await screen.findByText('TEA-01');
    expect(listRequests).toEqual([{ page: '2', pageSize: '20', sortBy: 'createdAt', sortDir: 'desc', search: 'web' }]);
    expect(document.querySelector('.ant-pagination-item-active')).toHaveTextContent('2');
    expect(header('Ngày tạo')).toHaveAttribute('aria-sort', 'descending');
    expect(header('SKU')).not.toHaveAttribute('aria-sort');
    expect(screen.getByRole('searchbox')).toHaveValue('web');

    // Sorting by the default field starts on page 1; the field is left out of the address but sent with its direction.
    await user.click(header('SKU'));
    await waitFor(() =>
      expect(listRequests.at(-1)).toEqual({ page: '1', pageSize: '20', sortBy: 'sku', sortDir: 'asc', search: 'web' }),
    );
    expect(location()).toBe(`${WEBSITE_PATH}?sortDir=asc&search=web`);

    await user.click(screen.getByRole('listitem', { name: '3' }));
    await waitFor(() => expect(listRequests.at(-1)?.page).toBe('3'));
    expect(location()).toBe(`${WEBSITE_PATH}?sortDir=asc&search=web&page=3`);

    await user.click(header('Ngày tạo'));
    await waitFor(() =>
      expect(listRequests.at(-1)).toEqual({
        page: '1',
        pageSize: '20',
        sortBy: 'createdAt',
        sortDir: 'asc',
        search: 'web',
      }),
    );
    expect(location()).toBe(`${WEBSITE_PATH}?sortDir=asc&search=web&sortBy=createdAt`);

    // One request per pause in typing; a new search starts on page 1.
    await user.click(screen.getByRole('listitem', { name: '2' }));
    await waitFor(() => expect(listRequests.at(-1)?.page).toBe('2'));
    const before = listRequests.length;
    await user.type(screen.getByRole('searchbox'), '-1');
    await waitFor(() => expect(listRequests.at(-1)).toMatchObject({ search: 'web-1', page: '1' }));
    expect(listRequests.length).toBe(before + 1);
    expect(location()).toBe(`${WEBSITE_PATH}?sortDir=asc&search=web-1&sortBy=createdAt`);
  });

  it('ignores a sort field in the address that the API does not know', async () => {
    const { listRequests } = serveChannelDetail(websiteChannel, [teaMapping]);
    renderDetail(`${WEBSITE_PATH}?sortBy=status&sortDir=desc`);

    await screen.findByText('TEA-01');
    // The direction applies to the default field.
    expect(listRequests).toEqual([{ page: '1', pageSize: '20', sortBy: 'sku', sortDir: 'desc' }]);
  });

  it.each([
    ['Staff', staffUser, false],
    ['Manager', managerUser, true],
    ['Admin', adminUser, true],
  ] as const)('%s sees create and edit: %s', async (_role, user, canWrite) => {
    serveChannelDetail(websiteChannel, [teaMapping, coffeeMapping]);
    renderDetail(WEBSITE_PATH, user);

    await screen.findByText('TEA-01');
    expect(screen.queryAllByRole('button', { name: /Gắn sản phẩm/ })).toHaveLength(canWrite ? 1 : 0);
    expect(screen.queryAllByRole('button', { name: 'Sửa' })).toHaveLength(canWrite ? 2 : 0);
    expect([...document.querySelectorAll('thead th')].some((th) => th.textContent === 'Thao tác')).toBe(canWrite);
    expect(columnTexts('SKU')).toEqual(['TEA-01', 'COF-01']);
  });
});

describe('channel detail: mapping a product', () => {
  /**
   * The website channel without mappings, with a live hub connection. A new mapping is answered with the tea mapping and
   * the stock-sync task the API started for it (Pending).
   */
  async function renderUnmappedLive() {
    const detail = serveChannelDetail(websiteChannel, []);
    serveProductCatalog([teaProduct, coffeeProduct]);
    const taskBackend = serveTask(syncTask('Pending'));
    detail.state.createAnswer = () => {
      detail.state.items = [teaMapping];
      return HttpResponse.json(savedMapping(teaMapping, SYNC_TASK_ID), { status: 201 });
    };
    const hub = fakeHub({ autoAccept: true });
    const app = renderDetail(WEBSITE_PATH, managerUser, hub.create);
    await screen.findByTestId('channel-summary');
    await waitFor(() => expect(screen.getByTestId('connection-indicator')).toHaveTextContent('Trực tiếp'));
    return { ...detail, ...app, hub, taskBackend };
  }

  /** The create dialog with tea and the external SKU WEB-TEA, not sent yet. */
  async function fillTeaMapping(user: AppUser) {
    const dialog = await openCreateDialog(user);
    await chooseProduct(user, within(dialog).getByLabelText('Sản phẩm'), 'TEA-01 — Trà xanh');
    await user.type(within(dialog).getByLabelText('SKU trên kênh'), 'WEB-TEA');
    return dialog;
  }

  const createButton = (dialog: HTMLElement) => within(dialog).getByRole('button', { name: 'Tạo' });

  it('sends the mapping, refreshes the list, and shows the stock-sync task the API started', async () => {
    const { creates, listRequests, taskBackend, user } = await renderUnmappedLive();
    await pause(FLUSH_WINDOW_MS + 300);
    // Mappings are no realtime data: the connection did not fetch the list again.
    expect(listRequests).toHaveLength(1);

    const dialog = await fillTeaMapping(user);
    // The channel is shown, not chosen.
    expect(within(dialog).getByDisplayValue('Website')).toBeDisabled();
    expect(within(dialog).getByLabelText('SKU trên kênh')).toHaveAttribute('maxlength', '128');
    expect(within(dialog).getByLabelText('Mã sản phẩm trên kênh')).toHaveAttribute('maxlength', '128');
    await user.click(createButton(dialog));

    expect(await screen.findByText('Đã gắn sản phẩm vào kênh.')).toBeInTheDocument();
    // An external id that was not typed is sent as null; the status is not part of a new mapping.
    expect(creates).toEqual([
      { channelId: WEBSITE_ID, body: { productId: TEA_ID, externalProductId: null, externalSku: 'WEB-TEA' } },
    ]);
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(columnTexts('SKU')).toEqual(['TEA-01']));
    expect(listRequests).toHaveLength(2);

    // What the API answered: a sync task. Its status is the task's, not a guess.
    const result = await screen.findByTestId('sync-result');
    expect(result).toHaveTextContent('Đã bắt đầu đồng bộ tồn kho cho TEA-01.');
    expect(await within(result).findByText('Đang chờ')).toBeInTheDocument();
    expect(within(result).getByRole('link', { name: 'Mở tác vụ' })).toHaveAttribute(
      'href',
      `/processing/tasks/${SYNC_TASK_ID}`,
    );
    expect(taskBackend.reads).toEqual([SYNC_TASK_ID]);

    // Dismissed by the user.
    await user.click(within(result).getByRole('button', { name: 'Đóng' }));
    await waitFor(() => expect(screen.queryByTestId('sync-result')).not.toBeInTheDocument());
  });

  it('follows the sync task live: only a hint for this task fetches it again, and the mapping list is left alone', async () => {
    const { hub, listRequests, taskBackend, user } = await renderUnmappedLive();
    await user.click(createButton(await fillTeaMapping(user)));
    const result = await screen.findByTestId('sync-result');
    await within(result).findByText('Đang chờ');
    await waitFor(() => expect(listRequests).toHaveLength(2));
    expect(taskBackend.reads).toHaveLength(1);

    // A hint about another task asks for nothing; the hint for this task fetches it again.
    await hub.latest().send([{ type: 'processing.task.changed', taskId: TASK_ID, taskType: 'OrderProcessing' }]);
    await pause(FLUSH_WINDOW_MS + 300);
    expect(taskBackend.reads).toHaveLength(1);

    taskBackend.state.detail = syncTask('Succeeded');
    await hub
      .latest()
      .send([{ type: 'processing.task.changed', taskId: SYNC_TASK_ID, taskType: 'StockSync', status: 'Succeeded' }]);
    expect(await within(result).findByText('Thành công')).toBeInTheDocument();
    expect(taskBackend.reads).toHaveLength(2);
    expect(screen.getByTestId('sync-result')).toBe(result);
    // The mapping list is not touched by task hints.
    expect(listRequests).toHaveLength(2);
  });

  it('shows a failed sync task as failed, with the way to its attempts', async () => {
    const { state } = serveChannelDetail(websiteChannel, []);
    serveProductCatalog([teaProduct]);
    serveTask(syncTask('Failed'));
    state.createAnswer = () => HttpResponse.json(savedMapping(teaMapping, SYNC_TASK_ID), { status: 201 });
    const { user } = renderDetail();

    const dialog = await openCreateDialog(user);
    await chooseProduct(user, within(dialog).getByLabelText('Sản phẩm'), 'TEA-01 — Trà xanh');
    await user.type(within(dialog).getByLabelText('SKU trên kênh'), 'SIM-PERM-1');
    await user.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    const result = await screen.findByTestId('sync-result');
    expect((await within(result).findByText('Thất bại')).closest('.ant-tag')).toHaveClass('ant-tag-error');
    expect(within(result).getByRole('link', { name: 'Mở tác vụ' })).toBeInTheDocument();
  });

  it('says that no stock sync was started when the API started none, with the reasons the API documents - and asks for no task', async () => {
    const { state, creates } = serveChannelDetail(shopChannel, []);
    serveProductCatalog([teaProduct]);
    const saved = { ...teaMapping, channelId: SHOP_ID, externalSku: null };
    state.createAnswer = () => {
      state.items = [saved];
      return HttpResponse.json(savedMapping(saved, null), { status: 201 });
    };
    // No handler for a task: a request for one would fail this test.
    const { user } = renderDetail(SHOP_PATH);

    const dialog = await openCreateDialog(user);
    expect(within(dialog).getByDisplayValue('Cửa hàng mới')).toBeDisabled();
    await chooseProduct(user, within(dialog).getByLabelText('Sản phẩm'), 'TEA-01 — Trà xanh');
    await user.type(within(dialog).getByLabelText('Mã sản phẩm trên kênh'), 'WEB-1001');
    await user.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    const result = await screen.findByTestId('sync-result');
    expect(result).toHaveTextContent('Không có đồng bộ tồn kho nào được bắt đầu cho TEA-01.');
    expect(creates).toEqual([
      { channelId: SHOP_ID, body: { productId: TEA_ID, externalProductId: 'WEB-1001', externalSku: null } },
    ]);
    expect(within(result).queryByRole('link')).not.toBeInTheDocument();
    expect(screen.queryByTestId('sync-result-task')).not.toBeInTheDocument();

    // Which reason applied is not guessed: all of them are listed on hover.
    await user.hover(within(result).getByRole('img', { name: 'Vì sao?' }));
    expect(await screen.findByText(NO_SYNC_REASONS)).toBeInTheDocument();
  });

  it('puts the API errors where they belong and leaves list and result alone', async () => {
    const { state, creates, listRequests } = serveChannelDetail(websiteChannel, [coffeeMapping]);
    serveProductCatalog([teaProduct, coffeeProduct]);
    const { user } = renderDetail();
    const dialog = await openCreateDialog(user);
    const create = () => user.click(within(dialog).getByRole('button', { name: 'Tạo' }));
    const fieldErrors = () =>
      [...dialog.querySelectorAll('.ant-form-item-explain-error')].map((item) => item.textContent);

    // A product is asked for before anything is sent.
    await create();
    expect(await within(dialog).findByText('Hãy nhập thông tin cho trường Sản phẩm')).toBeInTheDocument();
    expect(creates).toEqual([]);

    // 400: "one of the two external ids" is the server's rule - nothing here stops the request - and its message
    // lands on the external product id.
    const oneIsNeeded = "Cần nhập 'Mã sản phẩm trên kênh' hoặc 'SKU trên kênh' (máy chủ).";
    state.createAnswer = () =>
      problem(400, { errorCode: 'validation_failed', errors: { externalProductId: [oneIsNeeded] } });
    await chooseProduct(user, within(dialog).getByLabelText('Sản phẩm'), 'COF-01 — Cà phê');
    await create();
    const needed = await within(dialog).findByText(oneIsNeeded);
    expect(needed.closest('.ant-form-item')).toContainElement(within(dialog).getByLabelText('Mã sản phẩm trên kênh'));
    expect(creates).toEqual([
      { channelId: WEBSITE_ID, body: { productId: COFFEE_ID, externalProductId: null, externalSku: null } },
    ]);

    // 409: the product is already mapped on this channel - on the product.
    state.createAnswer = () => problem(409, { errorCode: 'channel_product_exists' });
    await user.type(within(dialog).getByLabelText('Mã sản phẩm trên kênh'), 'WEB-1002');
    await create();
    const mapped = await within(dialog).findByText('Sản phẩm này đã được gắn trên kênh này.');
    expect(mapped.closest('.ant-form-item')).toContainElement(within(dialog).getByLabelText('Sản phẩm'));
    // The error of the previous attempt is replaced, not added to.
    await waitFor(() => expect(fieldErrors()).toEqual(['Sản phẩm này đã được gắn trên kênh này.']));

    // 409: the external product id belongs to another product - on the external product id.
    state.createAnswer = () => problem(409, { errorCode: 'external_product_id_taken' });
    await chooseProduct(user, within(dialog).getByLabelText('Sản phẩm'), 'TEA-01 — Trà xanh');
    await create();
    const taken = await within(dialog).findByText(
      'Mã sản phẩm trên kênh này đang thuộc về một sản phẩm khác của kênh.',
    );
    expect(taken.closest('.ant-form-item')).toContainElement(within(dialog).getByLabelText('Mã sản phẩm trên kênh'));
    expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument();

    // 404: the channel is gone - a form error.
    state.createAnswer = () => problem(404, { errorCode: 'not_found' });
    await create();
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Không tìm thấy dữ liệu.');

    // Four rejected requests: the dialog is still open with what was typed, the list was not fetched again, and
    // nothing claims that a sync was or was not started.
    expect(creates).toHaveLength(4);
    expect(within(dialog).getByLabelText('Mã sản phẩm trên kênh')).toHaveValue('WEB-1002');
    expect(listRequests).toHaveLength(1);
    expect(screen.queryByTestId('sync-result')).not.toBeInTheDocument();
    // Business answers are not unexpected errors.
    expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();
  });
});

describe('channel detail: changing a mapping', () => {
  it('changes the external ids and the status without a confirmation; product and channel are read-only; the next result replaces the previous one', async () => {
    const { state, updates, listRequests } = serveChannelDetail(websiteChannel, [teaMapping, coffeeMapping]);
    const taskBackend = serveTask({ ...syncTask('Succeeded'), productSku: 'COF-01' });
    const { user } = renderDetail();

    // Re-activating the coffee mapping with a second external id: the API starts a sync.
    const activated = { ...coffeeMapping, externalSku: 'WEB-COF', status: 'Active' as const };
    state.updateAnswer = () => {
      state.items = [teaMapping, activated];
      return HttpResponse.json(savedMapping(activated, SYNC_TASK_ID));
    };
    let dialog = await openEditDialog(user, 'COF-01');
    expect(within(dialog).getByDisplayValue('Website')).toBeDisabled();
    expect(within(dialog).getByDisplayValue('COF-01 — Cà phê')).toBeDisabled();
    expect(within(dialog).getByLabelText('Mã sản phẩm trên kênh')).toHaveValue('WEB-1002');
    expect(within(dialog).getByLabelText('SKU trên kênh')).toHaveValue('');
    // Its status, as the list has it.
    expect(dialog.querySelector('.ant-select')).toHaveTextContent('Ngừng');

    await user.type(within(dialog).getByLabelText('SKU trên kênh'), 'WEB-COF');
    await chooseOption(user, within(dialog).getByRole('combobox', { name: 'Trạng thái' }), 'Hoạt động');
    await user.click(within(dialog).getByRole('button', { name: 'Lưu' }));

    expect(await screen.findByText('Đã cập nhật liên kết sản phẩm.')).toBeInTheDocument();
    expect(updates).toEqual([
      {
        channelProductId: COFFEE_MAPPING_ID,
        body: { externalProductId: 'WEB-1002', externalSku: 'WEB-COF', status: 'Active' },
      },
    ]);
    // The status field is the action: no confirmation was asked for.
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(columnTexts('Trạng thái')).toEqual(['Hoạt động', 'Hoạt động']));
    expect(listRequests).toHaveLength(2);

    let result = await screen.findByTestId('sync-result');
    expect(result).toHaveTextContent('Đã bắt đầu đồng bộ tồn kho cho COF-01.');
    expect(await within(result).findByText('Thành công')).toBeInTheDocument();
    expect(taskBackend.reads).toEqual([SYNC_TASK_ID]);

    // Deactivating the tea mapping: the API starts none, and says so in place of the previous result.
    const deactivated = { ...teaMapping, status: 'Inactive' as const };
    state.updateAnswer = () => {
      state.items = [deactivated, activated];
      return HttpResponse.json(savedMapping(deactivated, null));
    };
    dialog = await openEditDialog(user, 'TEA-01');
    await chooseOption(user, within(dialog).getByRole('combobox', { name: 'Trạng thái' }), 'Ngừng');
    await user.click(within(dialog).getByRole('button', { name: 'Lưu' }));

    await waitFor(() =>
      expect(screen.getByTestId('sync-result')).toHaveTextContent(
        'Không có đồng bộ tồn kho nào được bắt đầu cho TEA-01.',
      ),
    );
    result = screen.getByTestId('sync-result');
    expect(screen.getAllByTestId('sync-result')).toHaveLength(1);
    expect(result).not.toHaveTextContent('COF-01');
    expect(screen.queryByTestId('sync-result-task')).not.toBeInTheDocument();
    expect(updates.at(-1)).toEqual({
      channelProductId: TEA_MAPPING_ID,
      body: { externalProductId: 'WEB-1001', externalSku: 'WEB-TEA', status: 'Inactive' },
    });
    await waitFor(() => expect(columnTexts('Trạng thái')).toEqual(['Ngừng', 'Hoạt động']));
    expect(listRequests).toHaveLength(3);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('never shows the status of the task of the previous save while the task of the next save is loading', async () => {
    const { state } = serveChannelDetail(websiteChannel, [teaMapping, coffeeMapping]);
    // The first task has succeeded; the second one is slow to load and still waiting.
    server.use(
      http.get(api('/processing/tasks/:id'), async ({ params }) => {
        if (params.id === SYNC_TASK_ID) {
          return HttpResponse.json(syncTask('Succeeded'));
        }

        await delay(600);
        return HttpResponse.json({ ...syncTask('Pending'), taskId: TASK_ID });
      }),
    );
    const { user } = renderDetail();
    const save = async (sku: string, externalSku: string, syncTaskId: string) => {
      state.updateAnswer = (channelProductId) =>
        HttpResponse.json(savedMapping(channelProductId === TEA_MAPPING_ID ? teaMapping : coffeeMapping, syncTaskId));
      const dialog = await openEditDialog(user, sku);
      await user.type(within(dialog).getByLabelText('SKU trên kênh'), externalSku);
      await user.click(within(dialog).getByRole('button', { name: 'Lưu' }));
    };

    await save('COF-01', 'WEB-COF', SYNC_TASK_ID);
    const result = await screen.findByTestId('sync-result');
    expect(await within(result).findByText('Thành công')).toBeInTheDocument();

    await save('TEA-01', '-2', TASK_ID);
    await waitFor(() => expect(result).toHaveTextContent('Đã bắt đầu đồng bộ tồn kho cho TEA-01.'));
    // Its task is not known yet: no status at all, least of all the other task's.
    expect(within(result).queryByText('Thành công')).not.toBeInTheDocument();
    expect(within(result).getByRole('link', { name: 'Mở tác vụ' })).toHaveAttribute(
      'href',
      `/processing/tasks/${TASK_ID}`,
    );

    expect(await within(result).findByText('Đang chờ')).toBeInTheDocument();
    expect(within(result).queryByText('Thành công')).not.toBeInTheDocument();
  });

  it('says that the mapping no longer exists (404) in the dialog and fetches the list again', async () => {
    const { state, listRequests } = serveChannelDetail(websiteChannel, [teaMapping, coffeeMapping]);
    const { user } = renderDetail();
    const dialog = await openEditDialog(user, 'COF-01');

    state.updateAnswer = () => {
      state.items = [teaMapping];
      return problem(404, { errorCode: 'channel_product_not_found' });
    };
    await user.type(within(dialog).getByLabelText('SKU trên kênh'), 'WEB-COF');
    await user.click(within(dialog).getByRole('button', { name: 'Lưu' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Liên kết sản phẩm không còn tồn tại.');
    // The list that offered the mapping was stale.
    await waitFor(() => expect(columnTexts('SKU')).toEqual(['TEA-01']));
    expect(listRequests).toHaveLength(2);
    expect(screen.queryByTestId('sync-result')).not.toBeInTheDocument();
    expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();
  });

  it('puts a taken external product id (409) and the validation error (400) on the field, without fetching the list again', async () => {
    const { state, updates, listRequests } = serveChannelDetail(websiteChannel, [teaMapping, coffeeMapping]);
    const { user } = renderDetail();
    const dialog = await openEditDialog(user, 'COF-01');
    const externalId = within(dialog).getByLabelText('Mã sản phẩm trên kênh');

    state.updateAnswer = () => problem(409, { errorCode: 'external_product_id_taken' });
    await user.clear(externalId);
    await user.type(externalId, 'WEB-1001');
    await user.click(within(dialog).getByRole('button', { name: 'Lưu' }));
    const taken = await within(dialog).findByText(
      'Mã sản phẩm trên kênh này đang thuộc về một sản phẩm khác của kênh.',
    );
    expect(taken.closest('.ant-form-item')).toContainElement(externalId);

    const oneIsNeeded = "Cần nhập 'Mã sản phẩm trên kênh' hoặc 'SKU trên kênh' (máy chủ).";
    state.updateAnswer = () =>
      problem(400, { errorCode: 'validation_failed', errors: { externalProductId: [oneIsNeeded] } });
    await user.clear(externalId);
    await user.click(within(dialog).getByRole('button', { name: 'Lưu' }));
    expect((await within(dialog).findByText(oneIsNeeded)).closest('.ant-form-item')).toContainElement(externalId);

    // A cleared field is sent as null; the status is sent as it was.
    expect(updates.map((update) => update.body)).toEqual([
      { externalProductId: 'WEB-1001', externalSku: null, status: 'Inactive' },
      { externalProductId: null, externalSku: null, status: 'Inactive' },
    ]);
    expect(listRequests).toHaveLength(1);
  });
});

describe('channel detail: mapping dialog reopened before its close animation has finished', () => {
  it('shows the mapping it was opened for, never the values of the previous one', async () => {
    serveChannelDetail(websiteChannel, [teaMapping, coffeeMapping]);
    // Animations on: in jsdom they never finish, so the closed dialog and its form stay mounted.
    const { user } = renderApp({ route: WEBSITE_PATH, session: sessionOf(managerUser), animations: true });

    let dialog = await openEditDialog(user, 'TEA-01');
    await waitFor(() => expect(within(dialog).getByLabelText('Mã sản phẩm trên kênh')).toHaveValue('WEB-1001'));
    await user.type(within(dialog).getByLabelText('SKU trên kênh'), '-EDITED');
    await user.click(within(dialog).getByRole('button', { name: 'Huỷ' }));

    dialog = await openEditDialog(user, 'COF-01');
    await waitFor(() => expect(within(dialog).getByLabelText('Mã sản phẩm trên kênh')).toHaveValue('WEB-1002'));
    // Coffee has no external SKU and is inactive: nothing of the tea mapping is left, typed or stored.
    expect(within(dialog).getByLabelText('SKU trên kênh')).toHaveValue('');
    expect(within(dialog).getByDisplayValue('COF-01 — Cà phê')).toBeDisabled();
    expect(dialog.querySelector('.ant-select')).toHaveTextContent('Ngừng');
  });
});

describe('channel detail: cache', () => {
  it('fetches the mapping list again after a product was changed (its SKU and name are shown), not after a rejected change', async () => {
    const { state, listRequests } = serveChannelDetail(websiteChannel, [teaMapping]);
    let reject = true;
    server.use(
      http.put(api('/products/:id'), () =>
        reject
          ? problem(400, { errorCode: 'validation_failed', errors: { name: ['Tên không hợp lệ (máy chủ).'] } })
          : HttpResponse.json({ ...teaProduct, name: 'Trà xanh mới' }),
      ),
    );
    const { store } = renderDetail(WEBSITE_PATH, staffUser);
    await screen.findByText('Trà xanh');
    expect(listRequests).toHaveLength(1);
    const changes = { name: 'Trà xanh mới', price: 10000, lowStockThreshold: 5, expiryDate: null };

    // What the product form does, from any page.
    await store.dispatch(productsApi.endpoints.updateProduct.initiate({ productId: TEA_ID, changes }));
    await pause(200);
    expect(listRequests).toHaveLength(1);

    reject = false;
    state.items = [{ ...teaMapping, productName: 'Trà xanh mới' }];
    await store.dispatch(productsApi.endpoints.updateProduct.initiate({ productId: TEA_ID, changes }));

    expect(await screen.findByText('Trà xanh mới')).toBeInTheDocument();
    expect(listRequests).toHaveLength(2);
  });

  it('fetches the mapping list again whenever the page is opened', async () => {
    const { listRequests } = serveChannelDetail(websiteChannel, [teaMapping]);
    serveChannels([websiteChannel]);
    const { user } = renderDetail(WEBSITE_PATH, staffUser);
    await screen.findByText('TEA-01');

    await user.click(screen.getByRole('button', { name: /Quay lại/ }));
    await user.click(await screen.findByRole('link', { name: 'Website' }));
    await screen.findByText('TEA-01');

    await waitFor(() => expect(listRequests).toHaveLength(2));
  });
});

describe('channel detail: language switch', () => {
  it('changes text only: the channel, the list state in the address, the result of the last save and the open dialog stay', async () => {
    const { state, channelReads, listRequests } = serveChannelDetail(websiteChannel, [teaMapping, coffeeMapping], 45);
    const address = `${WEBSITE_PATH}?search=web&sortDir=desc&page=2`;
    const { user, location } = renderDetail(address);

    // A save first, so that its result is on the page.
    const deactivated = { ...teaMapping, status: 'Inactive' as const };
    state.updateAnswer = () => {
      state.items = [deactivated, coffeeMapping];
      return HttpResponse.json(savedMapping(deactivated, null));
    };
    let dialog = await openEditDialog(user, 'TEA-01');
    await chooseOption(user, within(dialog).getByRole('combobox', { name: 'Trạng thái' }), 'Ngừng');
    await user.click(within(dialog).getByRole('button', { name: 'Lưu' }));
    const result = await screen.findByTestId('sync-result');
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(columnTexts('Trạng thái')).toEqual(['Ngừng', 'Ngừng']));

    // Then a dialog with something typed into it.
    dialog = await openEditDialog(user, 'COF-01');
    await user.type(within(dialog).getByLabelText('SKU trên kênh'), 'WEB-COF');

    await expectLanguageSwitchChangesTextOnly(user, location, {
      vietnamese: [
        'Sản phẩm trên kênh',
        'Mã trên kênh',
        'Có đồng bộ',
        TASKS_LINK,
        'Sửa liên kết sản phẩm',
        'Mã sản phẩm trên kênh',
        'Không có đồng bộ tồn kho nào được bắt đầu cho TEA-01.',
      ],
      english: [
        'Mapped products',
        'External ID',
        'Synced',
        'Stock-sync tasks of this channel',
        'Edit mapping',
        'External product ID',
        'No stock sync was started for TEA-01.',
      ],
      requestCount: () => channelReads.length + listRequests.length,
    });

    expect(location()).toBe(address);
    expect(screen.getByTestId('sync-result')).toBe(result);
    expect(within(dialog).getByLabelText('SKU trên kênh')).toHaveValue('WEB-COF');
    expect(within(dialog).getByDisplayValue('COF-01 — Cà phê')).toBeDisabled();
  });
});
