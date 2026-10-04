import { waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import type { Customer } from '@/features/customers/customersApi';
import type { Product } from '@/features/products/productsApi';
import type { PagedResult } from '@/shared/api/types';
import type { AppUser } from './interactions';
import { api, problem, server } from './server';

/** Product ids are GUIDs: pages validate ids taken from the address before sending them. */
export const TEA_ID = '11111111-1111-4111-8111-111111111111';
export const COFFEE_ID = '22222222-2222-4222-8222-222222222222';
export const SUGAR_ID = '33333333-3333-4333-8333-333333333333';

export function product(overrides: Partial<Product> & Pick<Product, 'productId' | 'sku' | 'name'>): Product {
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
    image: null,
    ...overrides,
  };
}

export const teaProduct = product({ productId: TEA_ID, sku: 'TEA-01', name: 'Trà xanh', availableStock: 1200 });
export const coffeeProduct = product({ productId: COFFEE_ID, sku: 'COF-01', name: 'Cà phê', availableStock: 0 });
export const sugarProduct = product({ productId: SUGAR_ID, sku: 'SUG-01', name: 'Đường', availableStock: 7 });

/**
 * The product endpoints a product picker uses: `GET /api/products` (the search matches SKU or name, like the API) and
 * `GET /api/products/{id}`. Records the query string of every list request and the id of every detail request.
 */
export function serveProductCatalog(products: Product[]) {
  const listRequests: Record<string, string>[] = [];
  const detailRequests: string[] = [];
  server.use(
    http.get(api('/products'), ({ request }) => {
      const url = new URL(request.url);
      listRequests.push(Object.fromEntries(url.searchParams));
      const search = url.searchParams.get('search')?.toLowerCase() ?? '';
      const items = products.filter(
        (p) => p.sku.toLowerCase().includes(search) || p.name.toLowerCase().includes(search),
      );
      const page: PagedResult<Product> = {
        items,
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 20),
        totalCount: items.length,
      };
      return HttpResponse.json(page);
    }),
    http.get(api('/products/:id'), ({ params }) => {
      detailRequests.push(String(params.id));
      const found = products.find((p) => p.productId === params.id);
      return found ? HttpResponse.json(found) : problem(404, { errorCode: 'not_found' });
    }),
  );
  return { listRequests, detailRequests };
}

/** The options of the picker (product, customer, any select) that is open right now. */
export function openOptions(): HTMLElement[] {
  return [
    ...document.querySelectorAll<HTMLElement>(
      '.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option',
    ),
  ];
}

/** The options of the product picker that is open right now. */
export const productOptions = openOptions;

/** What each product picker of the page shows: the label of its value, or its placeholder. */
export function pickerTexts(): string[] {
  return [...document.querySelectorAll('main .ant-select')].map((select) => select.textContent ?? '');
}

/**
 * Opens a product picker (its combobox) and picks the option whose label is `label` (`SKU — name`). Only the picker
 * that is open counts: the closed dropdowns of other pickers stay in the document with the same options.
 */
export async function chooseProduct(user: AppUser, combobox: HTMLElement, label: string) {
  await user.click(combobox);
  const option = await waitFor(() => {
    const found = productOptions().find((item) => item.textContent?.startsWith(label));
    if (!found) {
      throw new Error(`No product option "${label}" in the open picker`);
    }

    return found;
  });

  await user.click(option);
}

/** Customer ids are GUIDs too. */
export const AN_ID = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
export const BINH_ID = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';

export function customer(overrides: Partial<Customer> & Pick<Customer, 'customerId' | 'name'>): Customer {
  return {
    phone: null,
    email: null,
    address: null,
    createdAt: '2026-09-30T03:15:00+00:00',
    updatedAt: null,
    ...overrides,
  };
}

export const anCustomer = customer({ customerId: AN_ID, name: 'Nguyễn Văn An', phone: '+84 90 123 4567' });
export const binhCustomer = customer({ customerId: BINH_ID, name: 'Trần Thị Bình', email: 'binh@example.com' });

/**
 * The customer endpoints a customer picker uses: `GET /api/customers` (the search matches the name, phone or e-mail)
 * and `GET /api/customers/{id}`. Records the query string of every list request and the id of every detail request.
 */
export function serveCustomerCatalog(customers: Customer[]) {
  const listRequests: Record<string, string>[] = [];
  const detailRequests: string[] = [];
  server.use(
    http.get(api('/customers'), ({ request }) => {
      const url = new URL(request.url);
      listRequests.push(Object.fromEntries(url.searchParams));
      const search = url.searchParams.get('search')?.toLowerCase() ?? '';
      const items = customers.filter((c) =>
        [c.name, c.phone, c.email].some((text) => text?.toLowerCase().includes(search)),
      );
      const page: PagedResult<Customer> = {
        items,
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 20),
        totalCount: items.length,
      };
      return HttpResponse.json(page);
    }),
    http.get(api('/customers/:id'), ({ params }) => {
      detailRequests.push(String(params.id));
      const found = customers.find((c) => c.customerId === params.id);
      return found ? HttpResponse.json(found) : problem(404, { errorCode: 'not_found' });
    }),
  );
  return { listRequests, detailRequests };
}

/** Opens a customer picker (its combobox) and picks the option of the customer called `name`. */
export async function chooseCustomer(user: AppUser, combobox: HTMLElement, name: string) {
  await user.click(combobox);
  const option = await waitFor(() => {
    const found = openOptions().find((item) => item.textContent?.startsWith(name));
    if (!found) {
      throw new Error(`No customer option "${name}" in the open picker`);
    }

    return found;
  });

  await user.click(option);
}
