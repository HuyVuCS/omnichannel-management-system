import { baseApi } from '@/shared/api/baseApi';
import type { PagedResult } from '@/shared/api/types';
import type { SortDirection } from '@/shared/hooks/useListQueryState';

export interface Product {
  productId: string;
  sku: string;
  name: string;
  price: number;
  lowStockThreshold: number;
  /** `YYYY-MM-DD` or null. */
  expiryDate: string | null;
  totalStock: number;
  reservedStock: number;
  availableStock: number;
  /** The server's low-stock rule; the client only displays it. */
  isLowStock: boolean;
  createdAt: string;
  updatedAt: string | null;
}

/** `SKU — name`: the one way a product is named in pickers and summaries. */
export function productLabel(product: Pick<Product, 'sku' | 'name'>): string {
  return `${product.sku} — ${product.name}`;
}

/** Sort fields of `GET /api/products` (default: `sku`). */
export const PRODUCT_SORT_FIELDS = ['sku', 'name', 'price', 'createdAt'] as const;
export type ProductSortField = (typeof PRODUCT_SORT_FIELDS)[number];

export interface ProductListArgs {
  page: number;
  pageSize: number;
  search?: string;
  sortBy?: ProductSortField;
  sortDir?: SortDirection;
}

/** SKU is sent as typed: the server trims and upper-cases it and checks its format. */
export interface CreateProductRequest {
  sku: string;
  name: string;
  price: number;
  lowStockThreshold: number;
  expiryDate: string | null;
}

export type UpdateProductRequest = Omit<CreateProductRequest, 'sku'>;

const LIST_TAG = { type: 'Product', id: 'LIST' } as const;

/**
 * What a successful product change makes stale: the product list and the inventory list (it shows the product's name
 * and low-stock threshold). A rejected mutation changed nothing on the server and invalidates nothing.
 */
const listsOnSuccess = (_result: unknown, error: unknown) => (error ? [] : [LIST_TAG, 'Inventory' as const]);

/**
 * The mapping lists of the channels show a product's SKU and name (`channelsApi`; the tag is repeated here so that the
 * product endpoints do not depend on the channel feature).
 */
const CHANNEL_PRODUCT_LIST_TAG = { type: 'ChannelProduct', id: 'LIST' } as const;

/**
 * The alert lists show a product's SKU and name, and for a low-stock alert its threshold (`alertsApi`; repeated here
 * for the same reason).
 */
const ALERT_LIST_TAG = { type: 'Alert', id: 'LIST' } as const;

function compact(params: Record<string, string | number | undefined>) {
  return Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined && value !== ''));
}

export const productsApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    listProducts: build.query<PagedResult<Product>, ProductListArgs>({
      query: (args) => ({ url: 'products', params: compact({ ...args }) }),
      providesTags: [LIST_TAG],
    }),
    /** One product, e.g. to show the label of a product picker that was opened with an id from the address. */
    getProduct: build.query<Product, string>({
      query: (productId) => `products/${productId}`,
      providesTags: (_result, _error, productId) => [{ type: 'Product', id: productId }],
    }),
    createProduct: build.mutation<Product, CreateProductRequest>({
      query: (body) => ({ url: 'products', method: 'POST', body }),
      invalidatesTags: listsOnSuccess,
    }),
    updateProduct: build.mutation<Product, { productId: string; changes: UpdateProductRequest }>({
      query: ({ productId, changes }) => ({ url: `products/${productId}`, method: 'PUT', body: changes }),
      invalidatesTags: (result, error, { productId }) => [
        ...listsOnSuccess(result, error),
        ...(error ? [] : [{ type: 'Product' as const, id: productId }, CHANNEL_PRODUCT_LIST_TAG, ALERT_LIST_TAG]),
      ],
    }),
  }),
});

export const { useListProductsQuery, useGetProductQuery, useCreateProductMutation, useUpdateProductMutation } =
  productsApi;
