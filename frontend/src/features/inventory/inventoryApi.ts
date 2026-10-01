import { baseApi } from '@/shared/api/baseApi';
import type { PagedResult } from '@/shared/api/types';
import type { SortDirection } from '@/shared/hooks/useListQueryState';

/** The API lets Manager and Admin import stock; everyone signed in may read stock and movements. */
export const STOCK_IMPORT_ROLES = ['Admin', 'Manager'] as const;

export interface InventoryItem {
  productId: string;
  sku: string;
  name: string;
  totalStock: number;
  reservedStock: number;
  availableStock: number;
  lowStockThreshold: number;
  /** The server's low-stock rule; the client only displays it. */
  isLowStock: boolean;
  updatedAt: string;
}

/** Sort fields of `GET /api/inventory` (default: `sku`). */
export const INVENTORY_SORT_FIELDS = ['sku', 'name', 'availableStock', 'updatedAt'] as const;
export type InventorySortField = (typeof INVENTORY_SORT_FIELDS)[number];

export interface InventoryListArgs {
  page: number;
  pageSize: number;
  search?: string;
  sortBy?: InventorySortField;
  sortDir?: SortDirection;
  /** Only products the server considers low on stock. */
  lowStockOnly?: true;
}

/** Values of the API's `InventoryMovementType` and `InventoryReferenceType` enums (serialised as strings). */
export const MOVEMENT_TYPES = ['Import', 'Reserve', 'Release', 'Deduct', 'Adjustment'] as const;
export type MovementType = (typeof MOVEMENT_TYPES)[number];

export const REFERENCE_TYPES = ['Order', 'StockImport', 'ManualAdjustment'] as const;
export type ReferenceType = (typeof REFERENCE_TYPES)[number];

export interface InventoryMovement {
  inventoryMovementId: string;
  productId: string;
  sku: string;
  productName: string;
  type: MovementType;
  /** Quantity moved, always positive; the direction is given by `type`. */
  stock: number;
  referenceType: ReferenceType;
  referenceId: string | null;
  documentNumber: string | null;
  documentSeries: string | null;
  createdAt: string;
  /** A user id, or `system:{worker}` for background processing. */
  createdBy: string | null;
  /** E-mail of the user behind `createdBy`; null for system actors and unknown ids. */
  createdByEmail: string | null;
}

/** `GET /api/inventory/movements`: always newest first; `from`/`to` are ISO 8601 instants (inclusive). */
export interface MovementListArgs {
  page: number;
  pageSize: number;
  productId?: string;
  type?: MovementType;
  referenceType?: ReferenceType;
  referenceId?: string;
  from?: string;
  to?: string;
}

/** A goods-receipt document. The server applies it at most once (document series + number). */
export interface ImportInventoryRequest {
  documentNumber: string;
  documentSeries: string | null;
  items: { productId: string; stock: number }[];
}

export interface ImportedInventoryItem {
  productId: string;
  movementId: string;
  /** Stock of the product right after this import. */
  totalStock: number;
  reservedStock: number;
  availableStock: number;
}

export interface ImportInventoryResponse {
  /** `{documentSeries}/{documentNumber}`, or just the number without a series; the `referenceId` of the movements. */
  referenceId: string;
  /** In request order. */
  items: ImportedInventoryItem[];
}

function compact(params: Record<string, string | number | boolean | undefined>) {
  return Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined && value !== ''));
}

export const inventoryApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    listInventory: build.query<PagedResult<InventoryItem>, InventoryListArgs>({
      query: (args) => ({ url: 'inventory', params: compact({ ...args }) }),
      providesTags: ['Inventory'],
    }),
    listMovements: build.query<PagedResult<InventoryMovement>, MovementListArgs>({
      query: (args) => ({ url: 'inventory/movements', params: compact({ ...args }) }),
      providesTags: ['InventoryMovement'],
    }),
    importInventory: build.mutation<ImportInventoryResponse, ImportInventoryRequest>({
      query: (body) => ({ url: 'inventory/import', method: 'POST', body }),
      // An applied import changes stock everywhere it is shown; a rejected one changed nothing on the server.
      invalidatesTags: (_result, error) => (error ? [] : ['Inventory', 'InventoryMovement', 'Product']),
    }),
  }),
});

export const { useListInventoryQuery, useListMovementsQuery, useImportInventoryMutation } = inventoryApi;
