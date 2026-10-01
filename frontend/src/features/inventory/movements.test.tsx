import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import type { PagedResult } from '@/shared/api/types';
import { chooseProduct, coffeeProduct, COFFEE_ID, serveProductCatalog, teaProduct, TEA_ID } from '@/test/catalog';
import { chooseOption, columnTexts, expectLanguageSwitchChangesTextOnly, rowOf } from '@/test/interactions';
import { renderApp } from '@/test/renderApp';
import { api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';
import type { InventoryMovement } from './inventoryApi';

function movement(
  overrides: Partial<InventoryMovement> & Pick<InventoryMovement, 'inventoryMovementId' | 'type'>,
): InventoryMovement {
  return {
    productId: TEA_ID,
    sku: 'TEA-01',
    productName: 'Trà xanh',
    stock: 1,
    referenceType: 'StockImport',
    referenceId: null,
    documentNumber: null,
    documentSeries: null,
    createdAt: new Date(2026, 8, 30, 10, 15).toISOString(),
    createdBy: null,
    createdByEmail: null,
    ...overrides,
  };
}

const imported = movement({
  inventoryMovementId: 'm-import',
  type: 'Import',
  stock: 1234567,
  referenceType: 'StockImport',
  referenceId: 'S1/GRN-1',
  documentNumber: 'GRN-1',
  documentSeries: 'S1',
  createdBy: '7c0e7a54-1b61-4c35-9d5c-2f6f8f0f3a11',
  createdByEmail: 'manager@example.com',
});
const reserved = movement({
  inventoryMovementId: 'm-reserve',
  type: 'Reserve',
  stock: 3,
  referenceType: 'Order',
  referenceId: '0199a1b2-0000-7000-8000-000000000001',
  createdAt: new Date(2026, 9, 1, 8, 5).toISOString(),
  createdBy: 'system:order-worker',
});
const adjusted = movement({
  inventoryMovementId: 'm-adjust',
  type: 'Adjustment',
  stock: 2,
  referenceType: 'ManualAdjustment',
  referenceId: null,
  // A user that no longer exists: no e-mail, the raw value is shown.
  createdBy: 'deleted-user-id',
});

/** `GET /api/inventory/movements`; records the query string of every request. `answer` can replace the response. */
function serveMovements(initial: InventoryMovement[], totalCount = initial.length) {
  const state: { items: InventoryMovement[]; totalCount: number; answer?: () => Response } = {
    items: initial,
    totalCount,
  };
  const requests: Record<string, string>[] = [];
  server.use(
    http.get(api('/inventory/movements'), ({ request }) => {
      const url = new URL(request.url);
      requests.push(Object.fromEntries(url.searchParams));
      if (state.answer) {
        return state.answer();
      }

      const page: PagedResult<InventoryMovement> = {
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

function renderMovements(route = '/inventory/movements', user: AuthUser = staffUser) {
  return renderApp({ route, session: sessionOf(user) });
}

const startInput = () => screen.getByPlaceholderText('Từ ngày');
const endInput = () => screen.getByPlaceholderText('Đến ngày');

describe('movement history', () => {
  it('lists movements newest first as the API sends them: type, quantity, reference and who made them', async () => {
    const { requests } = serveMovements([reserved, imported, adjusted]);
    renderMovements();

    expect(await screen.findByText('manager@example.com')).toBeInTheDocument();
    expect(columnTexts('Thời gian')).toEqual(['01/10/2026 08:05', '30/09/2026 10:15', '30/09/2026 10:15']);
    expect(columnTexts('SKU')).toEqual(['TEA-01', 'TEA-01', 'TEA-01']);
    expect(columnTexts('Loại')).toEqual(['Giữ hàng', 'Nhập kho', 'Điều chỉnh']);
    expect(columnTexts('Số lượng')).toEqual(['3', '1.234.567', '2']);
    expect(columnTexts('Tham chiếu')).toEqual([
      'Đơn hàng: 0199a1b2-0000-7000-8000-000000000001',
      'Phiếu nhập kho: S1/GRN-1',
      'Điều chỉnh thủ công',
    ]);
    // E-mail of the user; "Hệ thống" for a background worker; the raw value when the API knows no e-mail.
    expect(columnTexts('Người thực hiện')).toEqual(['Hệ thống', 'manager@example.com', 'deleted-user-id']);

    // One status colour per type.
    expect(within(rowOf('manager@example.com')).getByText('Nhập kho').closest('.ant-tag')).toHaveClass(
      'ant-tag-success',
    );
    expect(within(rowOf('Hệ thống')).getByText('Giữ hàng').closest('.ant-tag')).toHaveClass('ant-tag-processing');

    // The API decides the order: nothing is sortable here, and there is no search box.
    expect(document.querySelectorAll('th.ant-table-column-has-sorters')).toHaveLength(0);
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
    expect(requests).toEqual([{ page: '1', pageSize: '20' }]);
  });

  it('shows the exact system actor as a tooltip', async () => {
    serveMovements([reserved]);
    const { user: appUser } = renderMovements();

    await appUser.hover(await screen.findByText('Hệ thống'));
    expect(await screen.findByRole('tooltip')).toHaveTextContent('system:order-worker');
  });

  it('restores every filter and the page from the address (reload) and sends the local day range with its offset', async () => {
    serveProductCatalog([teaProduct, coffeeProduct]);
    const { requests } = serveMovements([imported], 45);
    const address =
      `/inventory/movements?productId=${TEA_ID}&type=Import&referenceType=StockImport` +
      '&referenceId=S1%2FGRN-1&from=2026-09-01&to=2026-09-30&page=2';
    const { location } = renderMovements(address);

    expect(await screen.findByText('manager@example.com')).toBeInTheDocument();
    expect(requests).toHaveLength(1);
    const { from, to, ...rest } = requests[0]!;
    expect(rest).toEqual({
      page: '2',
      pageSize: '20',
      productId: TEA_ID,
      type: 'Import',
      referenceType: 'StockImport',
      referenceId: 'S1/GRN-1',
    });
    // The local start of the first day and the local end of the last day, written with the UTC offset.
    expect(from).toMatch(/^2026-09-01T00:00:00[+-]\d{2}:\d{2}$/);
    expect(new Date(from!).getTime()).toBe(new Date(2026, 8, 1, 0, 0, 0, 0).getTime());
    expect(to).toMatch(/^2026-09-30T23:59:59\.999[+-]\d{2}:\d{2}$/);
    expect(new Date(to!).getTime()).toBe(new Date(2026, 8, 30, 23, 59, 59, 999).getTime());

    // The controls show the filters.
    expect(await screen.findByText('TEA-01 — Trà xanh', { selector: '.ant-select *' })).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Loại biến động' }).closest('.ant-select')).toHaveTextContent(
      'Nhập kho',
    );
    expect(screen.getByRole('combobox', { name: 'Loại tham chiếu' }).closest('.ant-select')).toHaveTextContent(
      'Phiếu nhập kho',
    );
    expect(screen.getByRole('textbox', { name: 'Mã tham chiếu' })).toHaveValue('S1/GRN-1');
    expect(startInput()).toHaveValue('01/09/2026');
    expect(endInput()).toHaveValue('30/09/2026');
    expect(document.querySelector('.ant-pagination-item-active')).toHaveTextContent('2');
    expect(location()).toBe(address);
  });

  it('writes each filter to the address, starts on page 1 again, and "Clear filters" removes them all', async () => {
    serveProductCatalog([teaProduct, coffeeProduct]);
    const { requests } = serveMovements([imported, reserved], 45);
    const { user: appUser, location } = renderMovements('/inventory/movements?page=3');
    await screen.findByText('manager@example.com');
    expect(requests).toEqual([{ page: '3', pageSize: '20' }]);

    await chooseOption(appUser, screen.getByRole('combobox', { name: 'Loại biến động' }), 'Giữ hàng');
    await waitFor(() => expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', type: 'Reserve' }));
    expect(location()).toBe('/inventory/movements?type=Reserve');

    await appUser.click(screen.getByRole('listitem', { name: '2' }));
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ page: '2', type: 'Reserve' }));

    await chooseOption(appUser, screen.getByRole('combobox', { name: 'Loại tham chiếu' }), 'Đơn hàng');
    await waitFor(() =>
      expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20', type: 'Reserve', referenceType: 'Order' }),
    );

    await chooseProduct(appUser, screen.getByRole('combobox', { name: 'Sản phẩm' }), 'COF-01 — Cà phê');
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ productId: COFFEE_ID, page: '1' }));
    expect(location()).toBe(`/inventory/movements?type=Reserve&referenceType=Order&productId=${COFFEE_ID}`);

    // The reference is exact text; one request per pause in typing.
    const before = requests.length;
    await appUser.type(screen.getByRole('textbox', { name: 'Mã tham chiếu' }), 'ORD-7');
    await waitFor(() => expect(requests.at(-1)).toMatchObject({ referenceId: 'ORD-7', page: '1' }));
    expect(requests.length).toBe(before + 1);

    // A range of local days.
    await appUser.click(startInput());
    await appUser.type(startInput(), '05/09/2026{Enter}');
    await appUser.type(endInput(), '06/09/2026{Enter}');
    await waitFor(() => expect(requests.at(-1)?.to).toBeDefined());
    expect(new Date(requests.at(-1)!.from!).getTime()).toBe(new Date(2026, 8, 5).getTime());
    expect(new Date(requests.at(-1)!.to!).getTime()).toBe(new Date(2026, 8, 6, 23, 59, 59, 999).getTime());
    expect(location()).toBe(
      `/inventory/movements?type=Reserve&referenceType=Order&productId=${COFFEE_ID}` +
        '&referenceId=ORD-7&from=2026-09-05&to=2026-09-06',
    );

    await appUser.click(screen.getByRole('button', { name: 'Xoá bộ lọc' }));
    await waitFor(() => expect(requests.at(-1)).toEqual({ page: '1', pageSize: '20' }));
    expect(location()).toBe('/inventory/movements');
    expect(screen.getByRole('textbox', { name: 'Mã tham chiếu' })).toHaveValue('');
    expect(startInput()).toHaveValue('');
    expect(endInput()).toHaveValue('');
    expect(screen.queryByText('COF-01 — Cà phê', { selector: '.ant-select *' })).not.toBeInTheDocument();
    // Nothing writes the old reference back after the pause of the text box.
    await new Promise((resolve) => setTimeout(resolve, 500));
    expect(location()).toBe('/inventory/movements');
  });

  it('ignores address values that are not valid and never sends them', async () => {
    const { requests } = serveMovements([imported]);
    renderMovements(
      '/inventory/movements?productId=abc&type=Nope&referenceType=Anything&from=2026-13-45&to=yesterday&page=x',
    );

    await screen.findByText('manager@example.com');
    expect(requests).toEqual([{ page: '1', pageSize: '20' }]);
    expect(startInput()).toHaveValue('');
    expect(endInput()).toHaveValue('');
  });

  it('shows what the API rejects (an inverted range typed into the address) above the table, not an empty list', async () => {
    const { state, requests } = serveMovements([imported]);
    state.answer = () =>
      problem(400, {
        errorCode: 'validation_failed',
        errors: { to: ["'Đến ngày' không được sớm hơn 'Từ ngày'."] },
      });
    renderMovements('/inventory/movements?from=2026-09-02&to=2026-09-01');

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent("'Đến ngày' không được sớm hơn 'Từ ngày'.");
    expect(requests).toHaveLength(1);
    expect(requests[0]).toMatchObject({ page: '1' });
    expect(document.querySelectorAll('tbody tr.ant-table-row')).toHaveLength(0);
    // Not reported as an unexpected error.
    expect(screen.queryByTestId('correlation-id')).not.toBeInTheDocument();
  });

  it('shows a translated message for a rejection without field errors, and the list again after a fix', async () => {
    const { state } = serveMovements([imported]);
    state.answer = () => problem(403);
    const { user: appUser } = renderMovements();

    expect(await screen.findByRole('alert')).toHaveTextContent('Bạn không có quyền thực hiện thao tác này.');

    state.answer = undefined;
    await appUser.click(screen.getByRole('button', { name: /Làm mới/ }));
    expect(await screen.findByText('manager@example.com')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('a stock-import reference filters the history by that document; other references are plain text', async () => {
    const { requests } = serveMovements([reserved, imported]);
    const { user: appUser, location } = renderMovements(`/inventory/movements?type=Import&page=2`);
    await screen.findByText('manager@example.com');

    expect(screen.queryByRole('link', { name: /Đơn hàng:/ })).not.toBeInTheDocument();
    await appUser.click(screen.getByRole('link', { name: 'Phiếu nhập kho: S1/GRN-1' }));

    await waitFor(() =>
      expect(requests.at(-1)).toEqual({
        page: '1',
        pageSize: '20',
        referenceType: 'StockImport',
        referenceId: 'S1/GRN-1',
      }),
    );
    expect(location()).toBe('/inventory/movements?referenceType=StockImport&referenceId=S1%2FGRN-1');
    // The filter controls follow the address.
    expect(screen.getByRole('textbox', { name: 'Mã tham chiếu' })).toHaveValue('S1/GRN-1');
    expect(screen.getByRole('combobox', { name: 'Loại tham chiếu' }).closest('.ant-select')).toHaveTextContent(
      'Phiếu nhập kho',
    );
  });

  it('asks the API again on "Refresh"', async () => {
    const { state, requests } = serveMovements([imported]);
    const { user: appUser } = renderMovements();
    await screen.findByText('manager@example.com');

    state.items = [reserved, imported];
    await appUser.click(screen.getByRole('button', { name: /Làm mới/ }));

    expect(await screen.findByText('Hệ thống')).toBeInTheDocument();
    expect(requests).toHaveLength(2);
  });

  it('is readable by every role', async () => {
    serveMovements([imported]);
    renderMovements('/inventory/movements', managerUser);

    expect(await screen.findByText('manager@example.com')).toBeInTheDocument();
  });
});

describe('movement history: language switch', () => {
  it('changes text only: address, filters, their controls and the page stay', async () => {
    serveProductCatalog([teaProduct]);
    const { requests } = serveMovements([reserved, imported, adjusted], 45);
    const address =
      `/inventory/movements?productId=${TEA_ID}&type=Import&referenceType=StockImport` +
      '&referenceId=S1%2FGRN-1&from=2026-09-01&to=2026-09-30&page=2';
    const { user: appUser, location } = renderMovements(address);
    await screen.findByText('manager@example.com');
    await screen.findByText('TEA-01 — Trà xanh', { selector: '.ant-select *' });

    await expectLanguageSwitchChangesTextOnly(appUser, location, {
      vietnamese: ['Biến động tồn kho', 'Người thực hiện', 'Giữ hàng', 'Hệ thống', 'Xoá bộ lọc', '20 / trang'],
      english: ['Inventory movements', 'Made by', 'Reserve', 'System', 'Clear filters', '20 / page'],
      requestCount: () => requests.length,
    });

    expect(columnTexts('Thời gian')).toEqual(['01/10/2026 08:05', '30/09/2026 10:15', '30/09/2026 10:15']);
    expect(columnTexts('Số lượng')).toEqual(['3', '1.234.567', '2']);
    expect(columnTexts('Tham chiếu')[1]).toBe('Phiếu nhập kho: S1/GRN-1');
    expect(startInput()).toHaveValue('01/09/2026');
    expect(location()).toBe(address);
  });
});
