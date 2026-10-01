import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import { productsApi } from '@/features/products/productsApi';
import {
  chooseProduct,
  coffeeProduct,
  COFFEE_ID,
  pickerTexts,
  productOptions,
  serveProductCatalog,
  sugarProduct,
  teaProduct,
  TEA_ID,
} from '@/test/catalog';
import { columnTexts, expectLanguageSwitchChangesTextOnly, findDialog, type AppUser } from '@/test/interactions';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';
import { inventoryApi, type ImportInventoryResponse } from './inventoryApi';

const emptyPage = { items: [], page: 1, pageSize: 20, totalCount: 0 };

/** `POST /api/inventory/import`: records the bodies; `answer` decides the response of the next call. */
function serveImport() {
  const state: { answer: () => Response } = {
    answer: () =>
      HttpResponse.json<ImportInventoryResponse>({
        referenceId: 'S1/GRN-1',
        items: [
          { productId: TEA_ID, movementId: 'mov-1', totalStock: 1205, reservedStock: 5, availableStock: 1200 },
          { productId: COFFEE_ID, movementId: 'mov-2', totalStock: 10, reservedStock: 0, availableStock: 10 },
        ],
      }),
  };
  const bodies: unknown[] = [];
  server.use(
    http.post(api('/inventory/import'), async ({ request }) => {
      bodies.push(await request.json());
      return state.answer();
    }),
  );
  return { state, bodies };
}

/**
 * The lists an import makes stale, each watched by a subscription like an open page would have: the counters show
 * whether the import invalidated them (a refetch) or not.
 */
function watchStockLists(store: ReturnType<typeof renderApp>['store']) {
  const counts = { inventory: 0, movements: 0 };
  server.use(
    http.get(api('/inventory'), () => {
      counts.inventory += 1;
      return HttpResponse.json(emptyPage);
    }),
    http.get(api('/inventory/movements'), () => {
      counts.movements += 1;
      return HttpResponse.json(emptyPage);
    }),
  );
  void store.dispatch(inventoryApi.endpoints.listInventory.initiate({ page: 1, pageSize: 20 }));
  void store.dispatch(inventoryApi.endpoints.listMovements.initiate({ page: 1, pageSize: 20 }));
  // A product list other than the picker's (page size 5), so its requests can be told apart.
  void store.dispatch(productsApi.endpoints.listProducts.initiate({ page: 1, pageSize: 5 }));
  return counts;
}

function renderImport(route = '/inventory/import', user: AuthUser = managerUser) {
  return renderApp({ route, session: sessionOf(user) });
}

const documentNumber = () => screen.getByLabelText('Số chứng từ');
const documentSeries = () => screen.getByLabelText('Ký hiệu chứng từ');
const productOfLine = (line: number) => screen.getByRole('combobox', { name: `Sản phẩm, dòng ${line}` });
const quantityOfLine = (line: number) => screen.getByRole('spinbutton', { name: `Số lượng, dòng ${line}` });
const submitButton = () => screen.getByRole('button', { name: /Nhập kho$/ });

/** Document GRN-1 (series S1) with two lines: 5 × tea, 10 × coffee. */
async function fillTwoLines(appUser: AppUser) {
  await appUser.type(await screen.findByLabelText('Số chứng từ'), 'GRN-1');
  await appUser.type(documentSeries(), 'S1');
  await chooseProduct(appUser, productOfLine(1), 'TEA-01 — Trà xanh');
  await appUser.type(quantityOfLine(1), '5');
  await appUser.click(screen.getByRole('button', { name: /Thêm dòng/ }));
  await chooseProduct(appUser, productOfLine(2), 'COF-01 — Cà phê');
  await appUser.type(quantityOfLine(2), '10');
}

/** Submits the form and confirms. */
async function submitAndConfirm(appUser: AppUser) {
  await appUser.click(submitButton());
  const confirm = await findDialog('Xác nhận nhập kho?');
  await appUser.click(within(confirm).getByRole('button', { name: 'Xác nhận nhập' }));
}

describe('stock import', () => {
  it('imports a document with two lines after confirmation, shows the stock the API returned and refreshes the lists', async () => {
    const catalog = serveProductCatalog([teaProduct, coffeeProduct, sugarProduct]);
    const { bodies } = serveImport();
    const { user: appUser, location, store } = renderImport();
    const counts = watchStockLists(store);
    await waitFor(() => expect(counts).toEqual({ inventory: 1, movements: 1 }));
    const watchedProductLists = () => catalog.listRequests.filter((request) => request.pageSize === '5').length;
    await waitFor(() => expect(watchedProductLists()).toBe(1));

    await fillTwoLines(appUser);
    await appUser.click(submitButton());

    // Nothing is sent before the user has seen what will be imported.
    const confirm = await findDialog('Xác nhận nhập kho?');
    expect(bodies).toEqual([]);
    expect(confirm).toHaveTextContent('Số chứng từ');
    expect(confirm).toHaveTextContent('GRN-1');
    expect(confirm).toHaveTextContent('Ký hiệu chứng từ');
    expect(confirm).toHaveTextContent('S1');
    expect(within(confirm).getByText('Số dòng').closest('tr, .ant-descriptions-item')).toHaveTextContent('2');
    expect(within(confirm).getByText('Tổng số lượng').closest('tr, .ant-descriptions-item')).toHaveTextContent('15');
    expect(confirm).toHaveTextContent('Không thể hoàn tác thao tác nhập kho.');

    await appUser.click(within(confirm).getByRole('button', { name: 'Xác nhận nhập' }));

    // The result: the reference and, per line, the stock from the response.
    expect(await screen.findByText('Đã nhập kho')).toBeInTheDocument();
    expect(screen.getByText('Chứng từ: S1/GRN-1')).toBeInTheDocument();
    expect(bodies).toEqual([
      {
        documentNumber: 'GRN-1',
        documentSeries: 'S1',
        items: [
          { productId: TEA_ID, stock: 5 },
          { productId: COFFEE_ID, stock: 10 },
        ],
      },
    ]);
    expect(columnTexts('Sản phẩm')).toEqual(['TEA-01 — Trà xanh', 'COF-01 — Cà phê']);
    expect(columnTexts('Số lượng nhập')).toEqual(['5', '10']);
    expect(columnTexts('Tổng tồn')).toEqual(['1.205', '10']);
    expect(columnTexts('Đang giữ')).toEqual(['5', '0']);
    expect(columnTexts('Khả dụng')).toEqual(['1.200', '10']);
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(location()).toBe('/inventory/import');

    // Buttons whose text differs per language have a fixed width (found in the browser: they moved on a switch).
    expect(screen.getByRole('button', { name: 'Xem biến động của chứng từ này' })).toHaveStyle({ width: '280px' });
    expect(screen.getByRole('button', { name: 'Nhập chứng từ khác' })).toHaveStyle({ width: '176px' });

    // Inventory, movements and products were invalidated: every open list asked the API again.
    await waitFor(() => expect(counts).toEqual({ inventory: 2, movements: 2 }));
    await waitFor(() => expect(watchedProductLists()).toBe(2));

    // From here: the movements of exactly this document.
    server.use(http.get(api('/inventory/movements'), () => HttpResponse.json(emptyPage)));
    await appUser.click(screen.getByRole('button', { name: 'Xem biến động của chứng từ này' }));
    expect(await screen.findByRole('heading', { name: 'Biến động tồn kho' })).toBeInTheDocument();
    expect(location()).toBe('/inventory/movements?referenceType=StockImport&referenceId=S1%2FGRN-1');
  });

  it('sends no series when the field is left empty, and "new import" starts with an empty form', async () => {
    serveProductCatalog([teaProduct, coffeeProduct]);
    const { state, bodies } = serveImport();
    state.answer = () =>
      HttpResponse.json<ImportInventoryResponse>({
        referenceId: 'GRN-2',
        items: [{ productId: TEA_ID, movementId: 'mov-3', totalStock: 3, reservedStock: 0, availableStock: 3 }],
      });
    const { user: appUser } = renderImport();

    await appUser.type(await screen.findByLabelText('Số chứng từ'), 'GRN-2');
    await chooseProduct(appUser, productOfLine(1), 'TEA-01 — Trà xanh');
    await appUser.type(quantityOfLine(1), '3');
    await appUser.click(submitButton());
    const confirm = await findDialog('Xác nhận nhập kho?');
    expect(confirm).not.toHaveTextContent('Ký hiệu chứng từ');
    await appUser.click(within(confirm).getByRole('button', { name: 'Xác nhận nhập' }));

    expect(await screen.findByText('Chứng từ: GRN-2')).toBeInTheDocument();
    expect(bodies).toEqual([
      { documentNumber: 'GRN-2', documentSeries: null, items: [{ productId: TEA_ID, stock: 3 }] },
    ]);

    await appUser.click(screen.getByRole('button', { name: 'Nhập chứng từ khác' }));
    expect(await screen.findByLabelText('Số chứng từ')).toHaveValue('');
    expect(screen.queryByText('Đã nhập kho')).not.toBeInTheDocument();
    expect(screen.getAllByRole('spinbutton')).toHaveLength(1);
    expect(quantityOfLine(1)).toHaveValue('');
    expect(pickerTexts()).toEqual(['Chọn sản phẩm']);
  });

  it('pre-fills the first line from ?productId= with the label of that product', async () => {
    const catalog = serveProductCatalog([teaProduct, coffeeProduct]);
    const { state, bodies } = serveImport();
    state.answer = () =>
      HttpResponse.json<ImportInventoryResponse>({
        referenceId: 'GRN-3',
        items: [{ productId: TEA_ID, movementId: 'mov-4', totalStock: 1202, reservedStock: 0, availableStock: 1202 }],
      });
    const { user: appUser, location } = renderImport(`/inventory/import?productId=${TEA_ID}`);

    // The label comes from GET /api/products/{id}; the product list is not loaded for it.
    expect(await screen.findByText('TEA-01 — Trà xanh')).toBeInTheDocument();
    expect(catalog.detailRequests).toEqual([TEA_ID]);
    expect(catalog.listRequests).toEqual([]);

    await appUser.type(documentNumber(), 'GRN-3');
    await appUser.type(quantityOfLine(1), '2');
    await submitAndConfirm(appUser);

    expect(await screen.findByText('Chứng từ: GRN-3')).toBeInTheDocument();
    expect(bodies).toEqual([
      { documentNumber: 'GRN-3', documentSeries: null, items: [{ productId: TEA_ID, stock: 2 }] },
    ]);
    // The name of the pre-filled product is known in the result too.
    expect(columnTexts('Sản phẩm')).toEqual(['TEA-01 — Trà xanh']);
    // The pre-filled product belonged to that document: a new import starts empty.
    expect(location()).toBe('/inventory/import');
  });

  it('ignores a productId in the address that is not an id', async () => {
    const catalog = serveProductCatalog([teaProduct]);
    renderImport('/inventory/import?productId=not-an-id');

    expect(await screen.findByLabelText('Số chứng từ')).toBeInTheDocument();
    expect(catalog.detailRequests).toEqual([]);
  });

  it('sends nothing when the confirmation is cancelled, and keeps the form as it was', async () => {
    serveProductCatalog([teaProduct, coffeeProduct]);
    const { bodies } = serveImport();
    const { user: appUser } = renderImport();
    await fillTwoLines(appUser);

    await appUser.click(submitButton());
    const confirm = await findDialog('Xác nhận nhập kho?');
    await appUser.click(within(confirm).getByRole('button', { name: 'Huỷ' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(bodies).toEqual([]);
    expect(documentNumber()).toHaveValue('GRN-1');
    expect(quantityOfLine(2)).toHaveValue('10');
    expect(screen.queryByText('Đã nhập kho')).not.toBeInTheDocument();
  });

  it('asks for the required fields before anything is confirmed', async () => {
    serveProductCatalog([teaProduct]);
    const { bodies } = serveImport();
    const { user: appUser } = renderImport();
    await screen.findByLabelText('Số chứng từ');

    // The two document fields stand side by side: their label areas have the same fixed height, so the inputs line up
    // although only one label carries the "required" mark (found in the browser: they were 2px apart).
    for (const input of [documentNumber(), documentSeries()]) {
      expect(input.closest('.ant-form-item')?.querySelector('.ant-form-item-label')).toHaveStyle({ height: '32px' });
    }

    await appUser.click(submitButton());

    expect(await screen.findByText('Hãy nhập thông tin cho trường Số chứng từ')).toBeInTheDocument();
    expect(screen.getByText('Hãy nhập thông tin cho trường Sản phẩm')).toBeInTheDocument();
    expect(screen.getByText('Hãy nhập thông tin cho trường Số lượng')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(bodies).toEqual([]);
  });
});

describe('stock import: errors of the API', () => {
  it('puts field errors on their fields and lines, list errors under the list, and invalidates nothing', async () => {
    serveProductCatalog([teaProduct, coffeeProduct]);
    const { state, bodies } = serveImport();
    state.answer = () =>
      problem(400, {
        errorCode: 'validation_failed',
        errors: {
          documentSeries: ["'Ký hiệu chứng từ' chỉ được dài tối đa 32 ký tự (máy chủ)."],
          'items[0].productId': ["Sản phẩm 'p-1' không tồn tại (máy chủ)."],
          'items[1].stock': ["'Số lượng' phải từ 1 đến 1000000 (máy chủ)."],
          items: ["'Các dòng hàng' không được chứa cùng một sản phẩm nhiều lần (máy chủ)."],
        },
      });
    const { user: appUser, store } = renderImport();
    const counts = watchStockLists(store);
    await waitFor(() => expect(counts).toEqual({ inventory: 1, movements: 1 }));
    await fillTwoLines(appUser);

    await submitAndConfirm(appUser);

    const seriesError = await screen.findByText("'Ký hiệu chứng từ' chỉ được dài tối đa 32 ký tự (máy chủ).");
    expect(seriesError.closest('.ant-form-item')).toContainElement(documentSeries());
    const productError = screen.getByText("Sản phẩm 'p-1' không tồn tại (máy chủ).");
    expect(productError.closest('.ant-form-item')).toContainElement(productOfLine(1));
    expect(productError.closest('.ant-form-item')).not.toContainElement(productOfLine(2));
    const quantityError = screen.getByText("'Số lượng' phải từ 1 đến 1000000 (máy chủ).");
    expect(quantityError.closest('.ant-form-item')).toContainElement(quantityOfLine(2));
    expect(quantityError.closest('.ant-form-item')).not.toContainElement(quantityOfLine(1));
    // The error of the list as a whole: under the lines, on none of them.
    const listError = screen.getByText("'Các dòng hàng' không được chứa cùng một sản phẩm nhiều lần (máy chủ).");
    expect(listError.closest('.ant-form-item')).toContainElement(productOfLine(1));
    expect(listError.closest('.ant-form-item')).toContainElement(quantityOfLine(2));

    // The confirmation is closed, the form keeps its values, and no list was refetched.
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(bodies).toHaveLength(1);
    expect(documentNumber()).toHaveValue('GRN-1');
    expect(screen.queryByText('Đã nhập kho')).not.toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(counts).toEqual({ inventory: 1, movements: 1 });
  });

  it('shows a document that was already imported on the document number, and the stock limit above the form', async () => {
    serveProductCatalog([teaProduct, coffeeProduct]);
    const { state, bodies } = serveImport();
    state.answer = () => problem(409, { errorCode: 'stock_import_already_applied' });
    const { user: appUser, store } = renderImport();
    const counts = watchStockLists(store);
    await waitFor(() => expect(counts).toEqual({ inventory: 1, movements: 1 }));
    await fillTwoLines(appUser);

    await submitAndConfirm(appUser);

    const applied = await screen.findByText('Chứng từ này đã được nhập kho. Hãy kiểm tra trong Biến động tồn kho.');
    expect(applied.closest('.ant-form-item')).toContainElement(documentNumber());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();

    state.answer = () => problem(409, { errorCode: 'stock_limit_exceeded' });
    await submitAndConfirm(appUser);

    expect(await screen.findByRole('alert')).toHaveTextContent('Số lượng nhập làm tồn kho vượt quá giới hạn cho phép.');
    // The error of the previous attempt is gone.
    expect(
      screen.queryByText('Chứng từ này đã được nhập kho. Hãy kiểm tra trong Biến động tồn kho.'),
    ).not.toBeInTheDocument();

    // The API stays the authority: a 403 is shown translated.
    state.answer = () => problem(403);
    await submitAndConfirm(appUser);
    expect(await screen.findByRole('alert')).toHaveTextContent('Bạn không có quyền thực hiện thao tác này.');

    expect(bodies).toHaveLength(3);
    expect(counts).toEqual({ inventory: 1, movements: 1 });
  });
});

describe('stock import: roles', () => {
  it('answers Staff with the no-access page and sends no request', async () => {
    const catalog = serveProductCatalog([teaProduct]);
    renderImport(`/inventory/import?productId=${TEA_ID}`, staffUser);

    expect(await screen.findByText('Không có quyền truy cập')).toBeInTheDocument();
    expect(screen.queryByLabelText('Số chứng từ')).not.toBeInTheDocument();
    expect(catalog.detailRequests).toEqual([]);
  });

  it.each([
    ['Manager', managerUser],
    ['Admin', adminUser],
  ] as const)('opens for %s', async (_role, user) => {
    serveProductCatalog([teaProduct]);
    renderImport('/inventory/import', user);

    expect(await screen.findByLabelText('Số chứng từ')).toBeInTheDocument();
  });
});

describe('product picker', () => {
  it('searches on the server after a pause in typing and shows SKU, name and available stock', async () => {
    const catalog = serveProductCatalog([teaProduct, coffeeProduct, sugarProduct]);
    const { user: appUser } = renderImport();

    // Nothing is loaded until the picker is opened.
    await screen.findByLabelText('Số chứng từ');
    expect(catalog.listRequests).toEqual([]);

    await appUser.click(productOfLine(1));
    await waitFor(() => expect(productOptions()).toHaveLength(3));
    expect(catalog.listRequests).toEqual([{ page: '1', pageSize: '20' }]);
    expect(productOptions().map((option) => option.textContent)).toEqual([
      'TEA-01 — Trà xanhKhả dụng: 1.200',
      'COF-01 — Cà phêKhả dụng: 0',
      'SUG-01 — ĐườngKhả dụng: 7',
    ]);

    // One request for the whole word, not one per key; the options are what the server found.
    await appUser.type(productOfLine(1), 'cà');
    await waitFor(() => expect(productOptions()).toHaveLength(1));
    expect(catalog.listRequests).toEqual([
      { page: '1', pageSize: '20' },
      { page: '1', pageSize: '20', search: 'cà' },
    ]);
    expect(productOptions()[0]).toHaveTextContent('COF-01 — Cà phê');
  });

  it('disables a product that is already on another line', async () => {
    serveProductCatalog([teaProduct, coffeeProduct]);
    const { user: appUser } = renderImport();
    await screen.findByLabelText('Số chứng từ');

    await chooseProduct(appUser, productOfLine(1), 'TEA-01 — Trà xanh');
    await appUser.click(screen.getByRole('button', { name: /Thêm dòng/ }));
    await appUser.click(productOfLine(2));

    await waitFor(() => expect(productOptions()).toHaveLength(2));
    const [teaOption, coffeeOption] = productOptions();
    expect(teaOption).toHaveClass('ant-select-item-option-disabled');
    expect(coffeeOption).not.toHaveClass('ant-select-item-option-disabled');

    // On its own line the chosen product stays selectable (it is the current value).
    await appUser.keyboard('{Escape}');
    await appUser.click(productOfLine(1));
    await waitFor(() => expect(productOptions()).toHaveLength(2));
    expect(productOptions()[0]).not.toHaveClass('ant-select-item-option-disabled');
  });

  it('removes a line, and the last line cannot be removed', async () => {
    serveProductCatalog([teaProduct, coffeeProduct]);
    const { user: appUser } = renderImport();
    await fillTwoLines(appUser);

    await appUser.click(screen.getByRole('button', { name: 'Xoá dòng 1' }));

    await waitFor(() => expect(screen.getAllByRole('spinbutton')).toHaveLength(1));
    expect(quantityOfLine(1)).toHaveValue('10');
    expect(pickerTexts()).toEqual(['COF-01 — Cà phê']);
    expect(screen.getByRole('button', { name: 'Xoá dòng 1' })).toBeDisabled();
  });
});

describe('stock import: language switch', () => {
  it('changes text only: the form with its lines and the open confirmation stay', async () => {
    const catalog = serveProductCatalog([teaProduct, coffeeProduct]);
    const { bodies } = serveImport();
    const { user: appUser, location } = renderImport();
    await fillTwoLines(appUser);
    await appUser.click(submitButton());
    await findDialog('Xác nhận nhập kho?');

    await expectLanguageSwitchChangesTextOnly(appUser, location, {
      vietnamese: ['Số chứng từ', 'Các dòng hàng', 'Thêm dòng', 'Xác nhận nhập kho?', 'Tổng số lượng', 'Xác nhận nhập'],
      english: [
        'Document number',
        'Lines',
        'Add line',
        'Confirm this stock import?',
        'Total quantity',
        'Confirm import',
      ],
      requestCount: () => catalog.listRequests.length + catalog.detailRequests.length + bodies.length,
    });

    // The lines are still there with their products and quantities, and the confirmation still shows the same import.
    expect(pickerTexts()).toEqual(['TEA-01 — Trà xanh', 'COF-01 — Cà phê']);
    expect(quantityOfLine(1)).toHaveValue('5');
    expect(quantityOfLine(2)).toHaveValue('10');
    const confirm = await findDialog('Xác nhận nhập kho?');
    expect(within(confirm).getByText('Tổng số lượng').closest('tr, .ant-descriptions-item')).toHaveTextContent('15');
    expect(bodies).toEqual([]);
    expect(location()).toBe('/inventory/import');
  });
});
