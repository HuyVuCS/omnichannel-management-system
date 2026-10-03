import { act, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import type { PagedResult } from '@/shared/api/types';
import { columnTexts, expectLanguageSwitchChangesTextOnly, findDialog, rowOf } from '@/test/interactions';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';
import type { Product } from './productsApi';

function product(overrides: Partial<Product> & Pick<Product, 'productId' | 'sku' | 'name'>): Product {
  return {
    price: 10000,
    lowStockThreshold: 5,
    expiryDate: null,
    totalStock: 0,
    reservedStock: 0,
    availableStock: 0,
    isLowStock: false,
    createdAt: '2026-09-30T03:15:00+00:00',
    updatedAt: null,
    ...overrides,
  };
}

// The flags deliberately contradict what a client-side rule would compute from stock and threshold.
const tea = product({
  productId: 'p-tea',
  sku: 'TEA-01',
  name: 'Trà xanh',
  price: 1234567,
  availableStock: 100,
  lowStockThreshold: 5,
  isLowStock: true,
  expiryDate: '2027-01-31',
});
const coffee = product({
  productId: 'p-coffee',
  sku: 'COF-01',
  name: 'Cà phê',
  price: 45000,
  availableStock: 0,
  lowStockThreshold: 10,
  isLowStock: false,
});

/** `GET /api/products` backed by a list the test can change; records the query string of every request. */
function serveProducts(initial: Product[], totalCount = initial.length) {
  const state = { products: initial, totalCount };
  const requests: Record<string, string>[] = [];
  server.use(
    http.get(api('/products'), ({ request }) => {
      const url = new URL(request.url);
      requests.push(Object.fromEntries(url.searchParams));
      const page: PagedResult<Product> = {
        items: state.products,
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 20),
        totalCount: state.totalCount,
      };
      return HttpResponse.json(page);
    }),
  );
  return { state, requests };
}

function renderProducts(user: AuthUser = managerUser, route = '/products') {
  return renderApp({ route, session: sessionOf(user) });
}

async function openCreateDialog(appUser: ReturnType<typeof renderApp>['user']) {
  await appUser.click(await screen.findByRole('button', { name: /Thêm sản phẩm/ }));
  return findDialog('Thêm sản phẩm');
}

describe('products page', () => {
  it('renders the page of products with money, dates and the low-stock tag exactly as the API says', async () => {
    const { requests } = serveProducts([tea, coffee], 2);
    renderProducts();

    expect(await screen.findByText('TEA-01')).toBeInTheDocument();
    expect(columnTexts('Tên sản phẩm')).toEqual(['Trà xanh', 'Cà phê']);
    expect(columnTexts('Giá')).toEqual(['1.234.567 ₫', '45.000 ₫']);
    expect(columnTexts('Tồn khả dụng')).toEqual(['100', '0']);
    expect(columnTexts('Hạn dùng')).toEqual(['31/01/2027', '']);

    // isLowStock comes from the API: 100 available is "low" and 0 available is "in stock" because the API said so.
    expect(columnTexts('Tình trạng')).toEqual(['Sắp hết', 'Đủ hàng']);

    expect(requests).toEqual([{ page: '1', pageSize: '20' }]);
  });

  it.each([
    ['Manager: with the action column', managerUser, 8],
    ['Staff: without it', staffUser, 7],
  ] as const)('fits the content area of a 1280 px window (%s)', async (_name, user, columnCount) => {
    serveProducts([tea, coffee]);
    renderProducts(user);
    await screen.findByText('TEA-01');

    // jsdom has no layout: the fixed column widths are what can be checked. 1280 px window - 240 px menu - 2 × 24 px
    // padding = 992 px, minus a vertical scrollbar: every column fits, so the table never scrolls sideways there.
    const widths = [...document.querySelectorAll<HTMLElement>('table colgroup col')].map((col) =>
      Number.parseInt(col.style.width, 10),
    );
    expect(widths).toHaveLength(columnCount);
    expect(document.querySelectorAll('thead th')).toHaveLength(columnCount);
    expect(widths.every((width) => width > 0)).toBe(true);
    expect(widths.reduce((sum, width) => sum + width, 0)).toBeLessThanOrEqual(976);
  });

  it('restores paging, sorting and search from the address (reload) and writes changes back to it', async () => {
    const { requests } = serveProducts([tea, coffee], 45);
    const { user: appUser, location } = renderProducts(
      managerUser,
      '/products?page=2&sortBy=price&sortDir=desc&search=tr',
    );

    await screen.findByText('TEA-01');
    expect(requests).toEqual([{ page: '2', pageSize: '20', sortBy: 'price', sortDir: 'desc', search: 'tr' }]);
    expect(document.querySelector('.ant-pagination-item-active')).toHaveTextContent('2');
    expect(screen.getByRole('columnheader', { name: /Giá/ })).toHaveAttribute('aria-sort', 'descending');
    expect(screen.getByRole('searchbox')).toHaveValue('tr');

    // Sorting by another column starts on page 1 and replaces the sort in the address.
    await appUser.click(screen.getByRole('columnheader', { name: /Tên sản phẩm/ }));
    await waitFor(() =>
      expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', sortBy: 'name', sortDir: 'asc', search: 'tr' }),
    );
    expect(location()).toBe('/products?sortBy=name&sortDir=asc&search=tr');

    // The default sort field is left out of the address but still sent with its direction.
    await appUser.click(screen.getByRole('columnheader', { name: /SKU/ }));
    await waitFor(() =>
      expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', sortBy: 'sku', sortDir: 'asc', search: 'tr' }),
    );
    expect(location()).toBe('/products?sortDir=asc&search=tr');

    await appUser.click(screen.getByRole('listitem', { name: '3' }));
    await waitFor(() => expect(requests.at(-1)?.page).toBe('3'));
    expect(location()).toBe('/products?sortDir=asc&search=tr&page=3');

    // One request per pause in typing; a new search starts on page 1.
    const before = requests.length;
    await appUser.type(screen.getByRole('searchbox'), 'a');
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ search: 'tra', page: '1' }));
    expect(requests.length).toBe(before + 1);
    expect(location()).toBe('/products?sortDir=asc&search=tra');
  });

  it('search box follows the address when it changes from outside (menu link, back), and never writes the old text back', async () => {
    const { requests } = serveProducts([tea, coffee]);
    const { user: appUser, location, router } = renderProducts(managerUser, '/products?search=abc&page=2');
    await screen.findByText('TEA-01');
    expect(screen.getByRole('searchbox')).toHaveValue('abc');
    expect(requests).toEqual([{ page: '2', pageSize: '20', search: 'abc' }]);

    // The menu item of the page the user is on: the plain list.
    await appUser.click(screen.getByRole('link', { name: 'Sản phẩm' }));

    await waitFor(() => expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20' }));
    expect(location()).toBe('/products');
    expect(screen.getByRole('searchbox')).toHaveValue('');
    // Longer than the debounce of the search box: nothing puts "abc" back.
    await new Promise((resolve) => setTimeout(resolve, 500));
    expect(location()).toBe('/products');
    expect(screen.getByRole('searchbox')).toHaveValue('');
    expect(requests).toHaveLength(2);

    // Back: the address has the search again, and so has the box.
    await act(() => router.navigate(-1));
    await waitFor(() => expect(screen.getByRole('searchbox')).toHaveValue('abc'));
    expect(location()).toBe('/products?search=abc&page=2');
    await new Promise((resolve) => setTimeout(resolve, 500));
    expect(location()).toBe('/products?search=abc&page=2');

    // Typing still works afterwards, and what is typed is not replaced when its own write reaches the address.
    await appUser.type(screen.getByRole('searchbox'), 'd');
    await waitFor(() => expect(location()).toBe('/products?search=abcd'));
    expect(screen.getByRole('searchbox')).toHaveValue('abcd');
  });

  it('creates a product: SKU as typed, whole VND amount, optional expiry date; then refreshes the list', async () => {
    const { state, requests } = serveProducts([coffee]);
    const bodies: unknown[] = [];
    server.use(
      http.post(api('/products'), async ({ request }) => {
        bodies.push(await request.json());
        state.products = [coffee, tea];
        return HttpResponse.json(tea, { status: 201 });
      }),
    );
    const { user: appUser } = renderProducts();

    const dialog = await openCreateDialog(appUser);
    const sku = within(dialog).getByLabelText('SKU');
    await appUser.type(sku, 'tea-01');
    // Shown upper-cased while typing; the value sent is what was typed (the server normalises it).
    expect(sku).toHaveStyle({ textTransform: 'uppercase' });
    await appUser.type(within(dialog).getByLabelText('Tên sản phẩm'), 'Trà xanh');
    await appUser.type(within(dialog).getByLabelText('Giá (VND)'), '1234567');
    expect(within(dialog).getByLabelText('Giá (VND)')).toHaveValue('1.234.567');
    expect(within(dialog).getByLabelText('Ngưỡng cảnh báo tồn kho')).toHaveValue('0');
    expect(within(dialog).getByText('0 = chỉ cảnh báo khi hết hàng.')).toBeInTheDocument();
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    expect(await screen.findByText('Đã thêm sản phẩm.')).toBeInTheDocument();
    expect(bodies).toEqual([
      { sku: 'tea-01', name: 'Trà xanh', price: 1234567, lowStockThreshold: 0, expiryDate: null },
    ]);
    expect(await screen.findByText('TEA-01')).toBeInTheDocument();
    expect(requests).toHaveLength(2);
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('puts server validation errors and the duplicate-SKU conflict on their fields, without refreshing the list', async () => {
    const { requests } = serveProducts([coffee]);
    let answer: 'validation' | 'conflict' | 'forbidden' = 'validation';
    server.use(
      http.post(api('/products'), () => {
        if (answer === 'validation') {
          return problem(400, {
            errorCode: 'validation_failed',
            errors: {
              sku: ["'sku' dài tối đa 64 ký tự, chỉ gồm A-Z, 0-9 (máy chủ)."],
              price: ["'price' phải lớn hơn hoặc bằng '0'."],
            },
          });
        }

        return answer === 'conflict' ? problem(409, { errorCode: 'product_sku_exists' }) : problem(403);
      }),
    );
    const { user: appUser } = renderProducts();

    const dialog = await openCreateDialog(appUser);
    await appUser.type(within(dialog).getByLabelText('SKU'), 'bad sku!');
    await appUser.type(within(dialog).getByLabelText('Tên sản phẩm'), 'Sản phẩm lỗi');
    await appUser.type(within(dialog).getByLabelText('Giá (VND)'), '1');
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    const skuError = await within(dialog).findByText("'sku' dài tối đa 64 ký tự, chỉ gồm A-Z, 0-9 (máy chủ).");
    expect(skuError.closest('.ant-form-item')).toContainElement(within(dialog).getByLabelText('SKU'));
    const priceError = within(dialog).getByText("'price' phải lớn hơn hoặc bằng '0'.");
    expect(priceError.closest('.ant-form-item')).toContainElement(within(dialog).getByLabelText('Giá (VND)'));

    answer = 'conflict';
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));
    const conflict = await within(dialog).findByText('Đã có sản phẩm với mã SKU này.');
    expect(conflict.closest('.ant-form-item')).toContainElement(within(dialog).getByLabelText('SKU'));

    // The API stays the authority: a 403 (e.g. the role changed meanwhile) is shown translated above the form.
    answer = 'forbidden';
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));
    expect(await within(dialog).findByText('Bạn không có quyền thực hiện thao tác này.')).toBeInTheDocument();

    expect(requests).toHaveLength(1);
    expect(within(dialog).getByLabelText('SKU')).toHaveValue('bad sku!');
  });

  it('edits a product: SKU read-only, other fields prefilled, expiry date kept; then refreshes the list', async () => {
    const { state, requests } = serveProducts([tea, coffee]);
    const updates: { id: string; body: unknown }[] = [];
    server.use(
      http.put(api('/products/:id'), async ({ request, params }) => {
        updates.push({ id: String(params.id), body: await request.json() });
        const renamed = { ...tea, name: 'Trà xanh Thái Nguyên' };
        state.products = [renamed, coffee];
        return HttpResponse.json(renamed);
      }),
    );
    const { user: appUser } = renderProducts();
    await screen.findByText('TEA-01');

    await appUser.click(within(rowOf('TEA-01')).getByRole('button', { name: 'Sửa' }));
    const dialog = await findDialog('Sửa sản phẩm');
    expect(within(dialog).getByLabelText('SKU')).toBeDisabled();
    expect(within(dialog).getByLabelText('SKU')).toHaveValue('TEA-01');
    expect(within(dialog).getByLabelText('Giá (VND)')).toHaveValue('1.234.567');
    expect(within(dialog).getByLabelText('Ngưỡng cảnh báo tồn kho')).toHaveValue('5');
    expect(within(dialog).getByLabelText('Hạn dùng (không bắt buộc)')).toHaveValue('31/01/2027');

    const name = within(dialog).getByLabelText('Tên sản phẩm');
    await appUser.clear(name);
    await appUser.type(name, 'Trà xanh Thái Nguyên');
    await appUser.click(within(dialog).getByRole('button', { name: 'Lưu' }));

    expect(await screen.findByText('Đã cập nhật sản phẩm.')).toBeInTheDocument();
    expect(updates).toEqual([
      {
        id: 'p-tea',
        body: { name: 'Trà xanh Thái Nguyên', price: 1234567, lowStockThreshold: 5, expiryDate: '2027-01-31' },
      },
    ]);
    expect(await screen.findByText('Trà xanh Thái Nguyên')).toBeInTheDocument();
    expect(requests).toHaveLength(2);
  });

  it('accepts an expiry date typed as DD/MM/YYYY and sends it as YYYY-MM-DD', async () => {
    serveProducts([coffee]);
    const bodies: { expiryDate?: string | null }[] = [];
    server.use(
      http.post(api('/products'), async ({ request }) => {
        bodies.push((await request.json()) as { expiryDate?: string | null });
        return HttpResponse.json(tea, { status: 201 });
      }),
    );
    const { user: appUser } = renderProducts();

    const dialog = await openCreateDialog(appUser);
    await appUser.type(within(dialog).getByLabelText('SKU'), 'DATE-01');
    await appUser.type(within(dialog).getByLabelText('Tên sản phẩm'), 'Có hạn dùng');
    await appUser.type(within(dialog).getByLabelText('Giá (VND)'), '5000');
    await appUser.type(within(dialog).getByLabelText('Hạn dùng (không bắt buộc)'), '31/12/2027{Enter}');
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]?.expiryDate).toBe('2027-12-31');
  });
});

describe('products page: roles', () => {
  it.each([
    ['Staff', staffUser, false],
    ['Manager', managerUser, true],
    ['Admin', adminUser, true],
  ] as const)('%s sees create and edit: %s', async (_role, user, canWrite) => {
    serveProducts([tea, coffee]);
    renderProducts(user);

    await screen.findByText('TEA-01');
    expect(screen.queryAllByRole('button', { name: /Thêm sản phẩm/ })).toHaveLength(canWrite ? 1 : 0);
    expect(screen.queryAllByRole('button', { name: 'Sửa' })).toHaveLength(canWrite ? 2 : 0);
    // Importing stock is for the same roles (task 012).
    expect(screen.queryAllByRole('button', { name: 'Nhập kho' })).toHaveLength(canWrite ? 2 : 0);
    // The data itself is visible to every role.
    expect(columnTexts('SKU')).toEqual(['TEA-01', 'COF-01']);
  });

  it('opens the stock-import form with the product of the row pre-filled', async () => {
    const teaWithId = { ...tea, productId: '11111111-1111-4111-8111-111111111111' };
    serveProducts([teaWithId, coffee]);
    server.use(http.get(api('/products/:id'), () => HttpResponse.json(teaWithId)));
    const { user: appUser, location } = renderProducts(managerUser);
    await screen.findByText('TEA-01');

    await appUser.click(within(rowOf('TEA-01')).getByRole('button', { name: 'Nhập kho' }));

    expect(await screen.findByRole('heading', { name: 'Nhập kho' })).toBeInTheDocument();
    expect(location()).toBe('/inventory/import?productId=11111111-1111-4111-8111-111111111111');
    expect(await screen.findByText('TEA-01 — Trà xanh')).toBeInTheDocument();
  });
});

describe('products page: language switch', () => {
  it('changes text only: address, table state, the open form and its values stay', async () => {
    const { requests } = serveProducts([tea, coffee], 45);
    const { user: appUser, location } = renderProducts(
      managerUser,
      '/products?page=2&sortBy=name&sortDir=desc&search=tr',
    );
    await screen.findByText('TEA-01');

    const dialog = await openCreateDialog(appUser);
    await appUser.type(within(dialog).getByLabelText('SKU'), 'NEW-01');
    await appUser.type(within(dialog).getByLabelText('Tên sản phẩm'), 'Đang nhập dở');
    await appUser.type(within(dialog).getByLabelText('Giá (VND)'), '99000');

    await expectLanguageSwitchChangesTextOnly(appUser, location, {
      vietnamese: ['Tên sản phẩm', 'Sắp hết', 'Ngưỡng cảnh báo tồn kho', '20 / trang'],
      english: ['Name', 'Low stock', 'Low-stock threshold', '20 / page'],
      requestCount: () => requests.length,
    });

    // Money and dates are formatted the same way in both languages.
    expect(columnTexts('Giá')).toEqual(['1.234.567 ₫', '45.000 ₫']);
    expect(columnTexts('Hạn dùng')).toEqual(['31/01/2027', '']);
    expect(location()).toBe('/products?page=2&sortBy=name&sortDir=desc&search=tr');
  });
});

describe('products page: dialog reopened before its close animation has finished', () => {
  it('shows the values of the record it was opened for, never those of the previous one', async () => {
    serveProducts([tea, coffee]);
    // Animations on: in jsdom they never finish, so the closed dialog and its form stay mounted.
    const { user: appUser } = renderApp({ route: '/products', session: sessionOf(managerUser), animations: true });
    await screen.findByText('TEA-01');

    await appUser.click(within(rowOf('TEA-01')).getByRole('button', { name: 'Sửa' }));
    let dialog = await findDialog('Sửa sản phẩm');
    await waitFor(() => expect(within(dialog).getByLabelText('Tên sản phẩm')).toHaveValue('Trà xanh'));
    await appUser.type(within(dialog).getByLabelText('Tên sản phẩm'), ' (chưa lưu)');
    await appUser.click(within(dialog).getByRole('button', { name: 'Huỷ' }));

    await appUser.click(within(rowOf('COF-01')).getByRole('button', { name: 'Sửa' }));
    dialog = await findDialog('Sửa sản phẩm');
    await waitFor(() => expect(within(dialog).getByLabelText('Tên sản phẩm')).toHaveValue('Cà phê'));
    expect(within(dialog).getByLabelText('SKU')).toHaveValue('COF-01');
    expect(within(dialog).getByLabelText('Giá (VND)')).toHaveValue('45.000');
    expect(within(dialog).getByLabelText('Ngưỡng cảnh báo tồn kho')).toHaveValue('10');
    expect(within(dialog).getByLabelText('Hạn dùng (không bắt buộc)')).toHaveValue('');
    await appUser.click(within(dialog).getByRole('button', { name: 'Huỷ' }));

    // ... and a create dialog opened right after an edit starts empty.
    await appUser.click(screen.getByRole('button', { name: /Thêm sản phẩm/ }));
    dialog = await findDialog('Thêm sản phẩm');
    await waitFor(() => expect(within(dialog).getByLabelText('Tên sản phẩm')).toHaveValue(''));
    expect(within(dialog).getByLabelText('SKU')).toHaveValue('');
    expect(within(dialog).getByLabelText('SKU')).toBeEnabled();
    expect(within(dialog).getByLabelText('Giá (VND)')).toHaveValue('');
  });

  it('reopened for the same record: shows its stored values, not what was typed and cancelled', async () => {
    serveProducts([tea, coffee]);
    // Animations on: the form stays mounted with the cancelled edit, and nothing about the record changes in between.
    const { user: appUser } = renderApp({ route: '/products', session: sessionOf(managerUser), animations: true });
    await screen.findByText('TEA-01');

    await appUser.click(within(rowOf('TEA-01')).getByRole('button', { name: 'Sửa' }));
    let dialog = await findDialog('Sửa sản phẩm');
    await waitFor(() => expect(within(dialog).getByLabelText('Tên sản phẩm')).toHaveValue('Trà xanh'));
    await appUser.type(within(dialog).getByLabelText('Tên sản phẩm'), ' (chưa lưu)');
    const threshold = within(dialog).getByLabelText('Ngưỡng cảnh báo tồn kho');
    await appUser.clear(threshold);
    await appUser.type(threshold, '99');
    expect(within(dialog).getByLabelText('Tên sản phẩm')).toHaveValue('Trà xanh (chưa lưu)');
    await appUser.click(within(dialog).getByRole('button', { name: 'Huỷ' }));

    await appUser.click(within(rowOf('TEA-01')).getByRole('button', { name: 'Sửa' }));
    dialog = await findDialog('Sửa sản phẩm');
    await waitFor(() => expect(within(dialog).getByLabelText('Tên sản phẩm')).toHaveValue('Trà xanh'));
    expect(within(dialog).getByLabelText('Ngưỡng cảnh báo tồn kho')).toHaveValue('5');
    expect(within(dialog).getByLabelText('Giá (VND)')).toHaveValue('1.234.567');
    expect(within(dialog).getByLabelText('Hạn dùng (không bắt buộc)')).toHaveValue('31/01/2027');
  });
});

/**
 * Animations off: the closed dialog and its form are destroyed, as in the browser once the close animation has ended.
 * The form instance outlives them. Without React's StrictMode (a production build; these tests) nothing else replaces
 * what it holds: the next dialog would start with the values of the previous one.
 */
describe('products page: dialog opened again after it was closed completely', () => {
  it('shows the values of the record it was opened for, never those of the previous one', async () => {
    serveProducts([tea, coffee]);
    const { user: appUser } = renderProducts();
    await screen.findByText('TEA-01');
    const closed = () => waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    await appUser.click(within(rowOf('TEA-01')).getByRole('button', { name: 'Sửa' }));
    let dialog = await findDialog('Sửa sản phẩm');
    expect(within(dialog).getByLabelText('Tên sản phẩm')).toHaveValue('Trà xanh');
    await appUser.type(within(dialog).getByLabelText('Tên sản phẩm'), ' (chưa lưu)');
    await appUser.click(within(dialog).getByRole('button', { name: 'Huỷ' }));
    await closed();

    await appUser.click(within(rowOf('COF-01')).getByRole('button', { name: 'Sửa' }));
    dialog = await findDialog('Sửa sản phẩm');
    expect(within(dialog).getByLabelText('Tên sản phẩm')).toHaveValue('Cà phê');
    expect(within(dialog).getByLabelText('SKU')).toHaveValue('COF-01');
    expect(within(dialog).getByLabelText('Giá (VND)')).toHaveValue('45.000');
    expect(within(dialog).getByLabelText('Ngưỡng cảnh báo tồn kho')).toHaveValue('10');
    expect(within(dialog).getByLabelText('Hạn dùng (không bắt buộc)')).toHaveValue('');
    await appUser.click(within(dialog).getByRole('button', { name: 'Huỷ' }));
    await closed();

    // The same record again: what was typed and not saved is gone.
    await appUser.click(within(rowOf('TEA-01')).getByRole('button', { name: 'Sửa' }));
    dialog = await findDialog('Sửa sản phẩm');
    expect(within(dialog).getByLabelText('Tên sản phẩm')).toHaveValue('Trà xanh');
    expect(within(dialog).getByLabelText('Hạn dùng (không bắt buộc)')).toHaveValue('31/01/2027');
    await appUser.click(within(dialog).getByRole('button', { name: 'Huỷ' }));
    await closed();

    // ... and a create dialog opened after an edit starts empty.
    await appUser.click(screen.getByRole('button', { name: /Thêm sản phẩm/ }));
    dialog = await findDialog('Thêm sản phẩm');
    expect(within(dialog).getByLabelText('Tên sản phẩm')).toHaveValue('');
    expect(within(dialog).getByLabelText('SKU')).toHaveValue('');
    expect(within(dialog).getByLabelText('Giá (VND)')).toHaveValue('');
    expect(within(dialog).getByLabelText('Ngưỡng cảnh báo tồn kho')).toHaveValue('0');
  });
});
