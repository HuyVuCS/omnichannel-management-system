import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import type { PagedResult } from '@/shared/api/types';
import { COFFEE_ID, serveProductCatalog, teaProduct, TEA_ID } from '@/test/catalog';
import { columnTexts, expectLanguageSwitchChangesTextOnly, rowOf, settled } from '@/test/interactions';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, managerUser, server, sessionOf, staffUser } from '@/test/server';
import type { InventoryItem } from './inventoryApi';

// The low-stock flags deliberately contradict what a client-side rule would compute from stock and threshold.
const tea: InventoryItem = {
  productId: TEA_ID,
  sku: 'TEA-01',
  name: 'Trà xanh',
  totalStock: 1234567,
  reservedStock: 34567,
  availableStock: 1200000,
  lowStockThreshold: 5,
  isLowStock: true,
  // 30/09/2026 10:15 in the time zone of the machine running the test.
  updatedAt: new Date(2026, 8, 30, 10, 15).toISOString(),
};
const coffee: InventoryItem = {
  productId: COFFEE_ID,
  sku: 'COF-01',
  name: 'Cà phê',
  totalStock: 0,
  reservedStock: 0,
  availableStock: 0,
  lowStockThreshold: 1000,
  isLowStock: false,
  updatedAt: new Date(2026, 9, 1, 8, 5).toISOString(),
};

/** `GET /api/inventory` backed by a list the test can change; records the query string of every request. */
function serveInventory(initial: InventoryItem[], totalCount = initial.length) {
  const state = { items: initial, totalCount };
  const requests: Record<string, string>[] = [];
  server.use(
    http.get(api('/inventory'), ({ request }) => {
      const url = new URL(request.url);
      requests.push(Object.fromEntries(url.searchParams));
      const page: PagedResult<InventoryItem> = {
        items: state.items,
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 20),
        totalCount: state.totalCount,
      };
      return HttpResponse.json(page);
    }),
  );
  return { state, requests };
}

/** What the pages reached from the inventory list request, so navigation in a test ends on a working page. */
function serveLinkedPages() {
  serveProductCatalog([teaProduct]);
  server.use(
    http.get(api('/inventory/movements'), () => HttpResponse.json({ items: [], page: 1, pageSize: 20, totalCount: 0 })),
  );
}

function renderInventory(user: AuthUser = managerUser, route = '/inventory') {
  return renderApp({ route, session: sessionOf(user) });
}

describe('inventory page', () => {
  it('shows stock per product exactly as the API says, with grouped numbers and the low-stock tag', async () => {
    const { requests } = serveInventory([tea, coffee], 2);
    renderInventory();

    expect(await screen.findByText('TEA-01')).toBeInTheDocument();
    expect(columnTexts('Tên sản phẩm')).toEqual(['Trà xanh', 'Cà phê']);
    expect(columnTexts('Tổng tồn')).toEqual(['1.234.567', '0']);
    expect(columnTexts('Đang giữ')).toEqual(['34.567', '0']);
    expect(columnTexts('Khả dụng')).toEqual(['1.200.000', '0']);
    expect(columnTexts('Ngưỡng cảnh báo')).toEqual(['5', '1.000']);
    expect(columnTexts('Cập nhật lúc')).toEqual(['30/09/2026 10:15', '01/10/2026 08:05']);

    // isLowStock comes from the API: 1.200.000 available is "low" and 0 available is "in stock" because the API said so.
    expect(columnTexts('Tình trạng')).toEqual(['Sắp hết', 'Đủ hàng']);
    // The same tag as on the Products page.
    expect(within(rowOf('TEA-01')).getByText('Sắp hết').closest('.ant-tag')).toHaveClass('ant-tag-warning');
    expect(within(rowOf('COF-01')).getByText('Đủ hàng').closest('.ant-tag')).toHaveClass('ant-tag-success');

    // Numbers are right-aligned.
    const available = within(rowOf('TEA-01')).getByText('1.200.000').closest('td');
    expect(available).toHaveStyle({ textAlign: 'right' });

    expect(requests).toEqual([{ page: '1', pageSize: '20' }]);
  });

  it('restores paging, sorting, search and "low stock only" from the address (reload) and writes changes back', async () => {
    const { requests } = serveInventory([tea, coffee], 45);
    const { user: appUser, location } = renderInventory(
      managerUser,
      '/inventory?page=2&sortBy=availableStock&sortDir=desc&search=tr&lowStockOnly=true',
    );

    await screen.findByText('TEA-01');
    expect(requests).toEqual([
      { page: '2', pageSize: '20', sortBy: 'availableStock', sortDir: 'desc', search: 'tr', lowStockOnly: 'true' },
    ]);
    expect(document.querySelector('.ant-pagination-item-active')).toHaveTextContent('2');
    expect(screen.getByRole('columnheader', { name: /Khả dụng/ })).toHaveAttribute('aria-sort', 'descending');
    expect(screen.getByRole('searchbox')).toHaveValue('tr');
    expect(screen.getByRole('switch')).toBeChecked();

    // Switching the filter off removes it from the address and starts on page 1.
    await appUser.click(screen.getByRole('switch'));
    await waitFor(() =>
      expect(requests.at(-1)).toEqual({
        page: '1',
        pageSize: '20',
        sortBy: 'availableStock',
        sortDir: 'desc',
        search: 'tr',
      }),
    );
    expect(location()).toBe('/inventory?sortBy=availableStock&sortDir=desc&search=tr');
    expect(screen.getByRole('switch')).not.toBeChecked();

    // Sorting by another column; the default sort field is left out of the address.
    await appUser.click(screen.getByRole('columnheader', { name: /Cập nhật lúc/ }));
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ sortBy: 'updatedAt', sortDir: 'asc', page: '1' }));
    await appUser.click(screen.getByRole('columnheader', { name: /SKU/ }));
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ sortBy: 'sku', sortDir: 'asc' }));
    expect(location()).toBe('/inventory?sortDir=asc&search=tr');

    await appUser.click(screen.getByRole('listitem', { name: '3' }));
    await waitFor(() => expect(requests.at(-1)?.page).toBe('3'));

    // Switching the filter on again: page 1, and the API is asked for low stock only.
    await appUser.click(screen.getByRole('switch'));
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ lowStockOnly: 'true', page: '1' }));
    expect(location()).toBe('/inventory?sortDir=asc&search=tr&lowStockOnly=true');

    // One request per pause in typing; a new search starts on page 1.
    const before = requests.length;
    await appUser.type(screen.getByRole('searchbox'), 'a');
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ search: 'tra', page: '1', lowStockOnly: 'true' }));
    expect(requests.length).toBe(before + 1);

    await settled();
  });

  it('never sends a "low stock only" value other than true', async () => {
    const { requests } = serveInventory([tea]);
    renderInventory(managerUser, '/inventory?lowStockOnly=maybe');

    await screen.findByText('TEA-01');
    expect(requests).toEqual([{ page: '1', pageSize: '20' }]);
    expect(screen.getByRole('switch')).not.toBeChecked();
  });

  it('asks the API again on "Refresh" (stock also changes in the background)', async () => {
    const { state, requests } = serveInventory([tea]);
    const { user: appUser } = renderInventory();
    await screen.findByText('TEA-01');
    expect(columnTexts('Đang giữ')).toEqual(['34.567']);

    // The Order Worker reserved stock meanwhile.
    state.items = [{ ...tea, reservedStock: 34570, availableStock: 1199997 }];
    await appUser.click(screen.getByRole('button', { name: /Làm mới/ }));

    await waitFor(() => expect(columnTexts('Đang giữ')).toEqual(['34.570']));
    expect(columnTexts('Khả dụng')).toEqual(['1.199.997']);
    expect(requests).toHaveLength(2);
    expect(requests[1]).toEqual(requests[0]);
  });
});

describe('inventory page: roles', () => {
  it.each([
    ['Staff', staffUser, false],
    ['Manager', managerUser, true],
    ['Admin', adminUser, true],
  ] as const)('%s sees the movement history of a product; import stock: %s', async (_role, user, canImport) => {
    serveInventory([tea, coffee]);
    renderInventory(user);

    await screen.findByText('TEA-01');
    expect(screen.getAllByRole('button', { name: 'Lịch sử biến động' })).toHaveLength(2);
    // One page action plus one per row - or none at all.
    expect(screen.queryAllByRole('button', { name: /Nhập kho/ })).toHaveLength(canImport ? 3 : 0);
    expect(screen.getByRole('button', { name: /Làm mới/ })).toBeInTheDocument();
    expect(columnTexts('SKU')).toEqual(['TEA-01', 'COF-01']);
  });

  it('opens the movement history and the import form of a product, and the empty import form', async () => {
    serveInventory([tea, coffee]);
    serveLinkedPages();
    const { user: appUser, location, router } = renderInventory(managerUser);
    await screen.findByText('TEA-01');

    await appUser.click(within(rowOf('TEA-01')).getByRole('button', { name: 'Lịch sử biến động' }));
    expect(await screen.findByRole('heading', { name: 'Biến động tồn kho' })).toBeInTheDocument();
    expect(location()).toBe(`/inventory/movements?productId=${TEA_ID}`);

    await router.navigate('/inventory');
    await appUser.click(within(await waitFor(() => rowOf('TEA-01'))).getByRole('button', { name: 'Nhập kho' }));
    expect(await screen.findByRole('heading', { name: 'Nhập kho' })).toBeInTheDocument();
    expect(location()).toBe(`/inventory/import?productId=${TEA_ID}`);
    // The import form has no menu item of its own: "Tồn kho" stays highlighted.
    expect(screen.getByRole('menuitem', { name: /Tồn kho$/ })).toHaveClass('ant-menu-item-selected');

    await router.navigate('/inventory');
    await screen.findByText('COF-01');
    const pageAction = screen
      .getAllByRole('button', { name: /Nhập kho/ })
      .find((button) => button.classList.contains('ant-btn-primary'));
    await appUser.click(pageAction!);
    expect(await screen.findByRole('heading', { name: 'Nhập kho' })).toBeInTheDocument();
    expect(location()).toBe('/inventory/import');
  });
});

describe('inventory page: language switch', () => {
  it('changes text only: address, filters and table state stay; numbers and dates keep their format', async () => {
    const { requests } = serveInventory([tea, coffee], 45);
    const { user: appUser, location } = renderInventory(
      managerUser,
      '/inventory?page=2&sortBy=name&sortDir=desc&search=tr&lowStockOnly=true',
    );
    await screen.findByText('TEA-01');

    await expectLanguageSwitchChangesTextOnly(appUser, location, {
      vietnamese: ['Tồn kho', 'Tổng tồn', 'Ngưỡng cảnh báo', 'Sắp hết', 'Chỉ hàng sắp hết', 'Làm mới', '20 / trang'],
      english: ['Inventory', 'Total', 'Low-stock threshold', 'Low stock', 'Low stock only', 'Refresh', '20 / page'],
      requestCount: () => requests.length,
    });

    expect(columnTexts('Tổng tồn')).toEqual(['1.234.567', '0']);
    expect(columnTexts('Cập nhật lúc')).toEqual(['30/09/2026 10:15', '01/10/2026 08:05']);
    expect(location()).toBe('/inventory?page=2&sortBy=name&sortDir=desc&search=tr&lowStockOnly=true');
    expect(screen.getByRole('switch')).toBeChecked();
  });
});
