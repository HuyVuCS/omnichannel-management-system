export const REPORTS_PATH = '/reports';

/** The API lets Manager and Admin export; kept next to the paths, like the roles of the other restricted pages. */
export const REPORTS_ROLES = ['Admin', 'Manager'] as const;

/** Filter of the exports table in the address, next to `page` / `pageSize` / `sortDir`. */
export const REPORT_TYPE_PARAM = 'reportType';

/**
 * What the "Export to Excel" button of a list hands to the Reports page: the list's filters as its address has them.
 * It travels in the router's location state, not in the address - the address of the Reports page belongs to its
 * table - and only fills the form in: nothing is requested until the user submits. The page checks every value again
 * (location state survives a reload and is not typed).
 */
export type ExportPrefill =
  | {
      reportType: 'Orders';
      channel?: string;
      status?: string;
      customerId?: string;
      search?: string;
      /** First and last local day (`YYYY-MM-DD`), when the list has a range. */
      from?: string;
      to?: string;
    }
  | { reportType: 'Inventory'; search?: string; lowStockOnly?: boolean };

export interface ReportsLocationState {
  exportPrefill: ExportPrefill;
}

/** Location state for a link or a `navigate` to the Reports page with a form filled in. */
export function exportPrefillState(prefill: ExportPrefill): ReportsLocationState {
  return { exportPrefill: prefill };
}

/** The prefill a location carries, when it carries one of a known report. Its fields are checked by the forms. */
export function readExportPrefill(state: unknown): ExportPrefill | undefined {
  const prefill = (state as Partial<ReportsLocationState> | null)?.exportPrefill;
  return typeof prefill === 'object' &&
    prefill !== null &&
    (prefill.reportType === 'Orders' || prefill.reportType === 'Inventory')
    ? prefill
    : undefined;
}
