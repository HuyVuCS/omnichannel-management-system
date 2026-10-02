export const INVENTORY_PATH = '/inventory';
export const STOCK_IMPORT_PATH = '/inventory/import';
export const MOVEMENTS_PATH = '/inventory/movements';

/**
 * The API lets Manager and Admin import stock; everyone signed in may read stock and movements. Kept next to the paths
 * (not in inventoryApi) because the route table needs it: the shell must not load a page's API code.
 */
export const STOCK_IMPORT_ROLES = ['Admin', 'Manager'] as const;

/** Address parameter of the inventory list's low-stock filter. */
export const LOW_STOCK_PARAM = 'lowStockOnly';

/** The inventory list with its low-stock filter on (the rows the dashboard counts as "low stock"). */
export function lowStockPath(): string {
  return `${INVENTORY_PATH}?${LOW_STOCK_PARAM}=true`;
}

/** The stock-import form, optionally with its first line pre-filled. */
export function importPath(productId?: string): string {
  return productId ? `${STOCK_IMPORT_PATH}?${new URLSearchParams({ productId }).toString()}` : STOCK_IMPORT_PATH;
}

/** The movement history filtered by a product or by a document (`referenceType` + `referenceId`). */
export function movementsPath(filter: { productId?: string; referenceType?: string; referenceId?: string }): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(filter)) {
    if (value) {
      params.set(key, value);
    }
  }

  const query = params.toString();
  return query ? `${MOVEMENTS_PATH}?${query}` : MOVEMENTS_PATH;
}
