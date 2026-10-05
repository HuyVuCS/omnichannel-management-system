import type { OrderStatus } from '@/features/orders/ordersApi';
import type { TaskStatus } from '@/features/processing/processingApi';
import { baseApi } from '@/shared/api/baseApi';
import type { PagedResult } from '@/shared/api/types';
import type { SortDirection } from '@/shared/hooks/useListQueryState';

/** Values of the API's `ReportType` enum (serialised as strings). */
export const REPORT_TYPES = ['Orders', 'Inventory'] as const;
export type ReportType = (typeof REPORT_TYPES)[number];

/** The status of the export's task, or `Expired`: its retention time is over and there is no file any more. */
export type ExportStatus = TaskStatus | 'Expired';

/** An export that is not finished yet: its row changes without the user doing anything. */
export function isUnfinishedExport(status: ExportStatus): boolean {
  return status === 'Pending' || status === 'InProgress' || status === 'RetryScheduled';
}

/**
 * An export as `GET /api/reports/exports` returns it (`ReportExportResponse`). The id is the id of its processing
 * task. `parameters` is the stored request: the filters as the API normalised them, `timeZone` and `language`.
 */
export interface ReportExport {
  taskId: string;
  reportType: ReportType;
  status: ExportStatus;
  parameters: Record<string, unknown> | null;
  fileName: string;
  /** Data rows of the file; null until it exists. */
  rowCount: number | null;
  sizeBytes: number | null;
  createdAt: string;
  completedAt: string | null;
  /** The file is kept until then. */
  expiresAt: string;
  /** The code of the last attempt of a `Failed` export; never an error text. */
  errorCode: string | null;
}

/** The list is sorted by the time of the request only; without a direction the API answers newest first. */
export const EXPORT_SORT_FIELDS = ['createdAt'] as const;

export interface ReportExportListArgs {
  page: number;
  pageSize: number;
  sortBy?: 'createdAt';
  sortDir?: SortDirection;
  reportType?: ReportType;
}

/**
 * Body of `POST /api/reports/orders/export`: the filters of the orders list with the same meaning, a required range
 * (`from` / `to` are instants, inclusive, on the creation time) and the IANA time zone the file's times are written in.
 */
export interface ExportOrdersRequest {
  channel?: string;
  status?: OrderStatus;
  customerId?: string;
  search?: string;
  from: string;
  to: string;
  timeZone: string;
}

/** Body of `POST /api/reports/inventory/export`: the filters of the inventory list and the time zone. */
export interface ExportInventoryRequest {
  search?: string;
  lowStockOnly: boolean;
  timeZone: string;
}

/** 202: the export is built in the background by the task with this id. */
export interface ExportAccepted {
  taskId: string;
}

/** A link that reads the file without the API's token: a credential until it expires, never kept or shown. */
export interface ExportDownload {
  url: string;
  expiresAt: string;
  fileName: string;
}

/** Every list of exports; a single export is tagged with its id. Realtime hints invalidate both (`features/realtime`). */
export const EXPORT_LIST_TAG = { type: 'ReportExport', id: 'LIST' } as const;

/** A request the API accepted added a row; a rejected one changed nothing on the server. */
const listOnSuccess = (_result: unknown, error: unknown) => (error ? [] : [EXPORT_LIST_TAG]);

function compact(params: Record<string, string | number | undefined>) {
  return Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined && value !== ''));
}

export const reportsApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    /** The caller's own exports: the API shows an export to nobody but its requester. */
    listReportExports: build.query<PagedResult<ReportExport>, ReportExportListArgs>({
      query: (args) => ({ url: 'reports/exports', params: compact({ ...args }) }),
      providesTags: (result) => [
        EXPORT_LIST_TAG,
        ...(result?.items ?? []).map((item) => ({ type: 'ReportExport' as const, id: item.taskId })),
      ],
    }),
    requestOrdersExport: build.mutation<ExportAccepted, ExportOrdersRequest>({
      query: (body) => ({ url: 'reports/orders/export', method: 'POST', body }),
      invalidatesTags: listOnSuccess,
    }),
    requestInventoryExport: build.mutation<ExportAccepted, ExportInventoryRequest>({
      query: (body) => ({ url: 'reports/inventory/export', method: 'POST', body }),
      invalidatesTags: listOnSuccess,
    }),
    /**
     * A mutation although it is a GET: every call must sign a new link, and a link must not sit in the query cache
     * where a later render (or a devtools dump) would find it. The page follows the link at once and resets the
     * mutation.
     */
    getExportDownload: build.mutation<ExportDownload, string>({
      query: (taskId) => `reports/exports/${taskId}/download`,
    }),
  }),
});

export const { useRequestOrdersExportMutation, useRequestInventoryExportMutation, useGetExportDownloadMutation } =
  reportsApi;
