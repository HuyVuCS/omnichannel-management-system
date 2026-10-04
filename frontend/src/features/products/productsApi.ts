import { baseApi } from '@/shared/api/baseApi';
import type { PagedResult } from '@/shared/api/types';
import type { SortDirection } from '@/shared/hooks/useListQueryState';
import { withStableUrl } from './imageLinks';
import type { UploadTarget } from './imageUpload';

/**
 * The image of a product. `url` reads it from the object storage without the API's token until `expiresAt`; the API
 * signs a new one for every response, `updatedAt` identifies the image.
 */
export interface ProductImage {
  url: string;
  expiresAt: string;
  contentType: string;
  sizeBytes: number;
  updatedAt: string;
}

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
  image: ProductImage | null;
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

/** The answer of the presign endpoint: the upload target and the key to confirm with afterwards. */
export interface PresignedImageUpload extends UploadTarget {
  objectKey: string;
  expiresAt: string;
}

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

/** Every product that enters the cache keeps the image link this client already uses (`withStableUrl`). */
const stable = (product: Product): Product => ({ ...product, image: withStableUrl(product.productId, product.image) });

/** What a changed image makes stale: the product and the product list - nothing else shows images. */
const imageChanged = (_result: unknown, error: unknown, { productId }: { productId: string }) =>
  error ? [] : [LIST_TAG, { type: 'Product' as const, id: productId }];

export const productsApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    listProducts: build.query<PagedResult<Product>, ProductListArgs>({
      query: (args) => ({ url: 'products', params: compact({ ...args }) }),
      transformResponse: (page: PagedResult<Product>) => ({ ...page, items: page.items.map(stable) }),
      providesTags: [LIST_TAG],
    }),
    /** One product, e.g. to show the label of a product picker that was opened with an id from the address. */
    getProduct: build.query<Product, string>({
      query: (productId) => `products/${productId}`,
      transformResponse: stable,
      providesTags: (_result, _error, productId) => [{ type: 'Product', id: productId }],
    }),
    createProduct: build.mutation<Product, CreateProductRequest>({
      query: (body) => ({ url: 'products', method: 'POST', body }),
      transformResponse: stable,
      invalidatesTags: listsOnSuccess,
    }),
    updateProduct: build.mutation<Product, { productId: string; changes: UpdateProductRequest }>({
      query: ({ productId, changes }) => ({ url: `products/${productId}`, method: 'PUT', body: changes }),
      transformResponse: stable,
      invalidatesTags: (result, error, { productId }) => [
        ...listsOnSuccess(result, error),
        ...(error ? [] : [{ type: 'Product' as const, id: productId }, CHANNEL_PRODUCT_LIST_TAG, ALERT_LIST_TAG]),
      ],
    }),
    /**
     * Step 1 of an image upload: a link for one upload to the storage. Nothing changes on the server, so nothing is
     * invalidated. The upload itself is not an endpoint of this API (`uploadToStorage`).
     */
    presignProductImage: build.mutation<
      PresignedImageUpload,
      { productId: string; contentType: string; sizeBytes: number }
    >({
      query: ({ productId, ...body }) => ({ url: `products/${productId}/image/presign`, method: 'POST', body }),
    }),
    /** Step 3: the uploaded object is checked by the API and becomes the product's image. */
    confirmProductImage: build.mutation<Product, { productId: string; objectKey: string }>({
      query: ({ productId, objectKey }) => ({ url: `products/${productId}/image`, method: 'PUT', body: { objectKey } }),
      transformResponse: stable,
      invalidatesTags: imageChanged,
    }),
    removeProductImage: build.mutation<void, { productId: string }>({
      query: ({ productId }) => ({ url: `products/${productId}/image`, method: 'DELETE' }),
      invalidatesTags: imageChanged,
    }),
  }),
});

export const {
  useListProductsQuery,
  useGetProductQuery,
  useCreateProductMutation,
  useUpdateProductMutation,
  usePresignProductImageMutation,
  useConfirmProductImageMutation,
  useRemoveProductImageMutation,
} = productsApi;
