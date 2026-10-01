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

/** Invalidate the list only when the mutation succeeded: a rejected one changed nothing on the server. */
const listOnSuccess = (_result: unknown, error: unknown) => (error ? [] : [LIST_TAG]);

function compact(params: Record<string, string | number | undefined>) {
  return Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined && value !== ''));
}

export const productsApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    listProducts: build.query<PagedResult<Product>, ProductListArgs>({
      query: (args) => ({ url: 'products', params: compact({ ...args }) }),
      providesTags: [LIST_TAG],
    }),
    createProduct: build.mutation<Product, CreateProductRequest>({
      query: (body) => ({ url: 'products', method: 'POST', body }),
      invalidatesTags: listOnSuccess,
    }),
    updateProduct: build.mutation<Product, { productId: string; changes: UpdateProductRequest }>({
      query: ({ productId, changes }) => ({ url: `products/${productId}`, method: 'PUT', body: changes }),
      invalidatesTags: listOnSuccess,
    }),
  }),
});

export const { useListProductsQuery, useCreateProductMutation, useUpdateProductMutation } = productsApi;
