export const INVENTORY_PATH = '/inventory';
export const STOCK_IMPORT_PATH = '/inventory/import';
export const MOVEMENTS_PATH = '/inventory/movements';

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
