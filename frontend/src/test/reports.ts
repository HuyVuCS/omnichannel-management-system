import { http, HttpResponse } from 'msw';
import type { ExportDownload, ReportExport } from '@/features/reports/reportsApi';
import type { PagedResult } from '@/shared/api/types';
import { AN_ID } from './catalog';
import { api, server } from './server';
import { STORAGE_ORIGIN } from './storage';

export const ORDERS_EXPORT_ID = '0199a1b2-0000-7000-8000-0000000000e1';
export const INVENTORY_EXPORT_ID = '0199a1b2-0000-7000-8000-0000000000e2';
export const FAILED_EXPORT_ID = '0199a1b2-0000-7000-8000-0000000000e3';
export const EXPIRED_EXPORT_ID = '0199a1b2-0000-7000-8000-0000000000e4';
export const PENDING_EXPORT_ID = '0199a1b2-0000-7000-8000-0000000000e5';

/** An export as the API returns it: a finished orders export of September 2026 unless the test says otherwise. */
export function reportExport(overrides: Partial<ReportExport> = {}): ReportExport {
  return {
    taskId: ORDERS_EXPORT_ID,
    reportType: 'Orders',
    status: 'Succeeded',
    parameters: {
      channel: 'pos',
      status: 'Reserved',
      customerId: AN_ID,
      search: 'DP-2026',
      // The stored range is in UTC; these are the first and the last instant of two local days.
      from: new Date(2026, 8, 1, 0, 0, 0, 0).toISOString(),
      to: new Date(2026, 8, 30, 23, 59, 59, 999).toISOString(),
      timeZone: 'Asia/Ho_Chi_Minh',
      language: 'vi',
    },
    fileName: 'orders_20261005-1030.xlsx',
    rowCount: 1234,
    sizeBytes: 4300,
    createdAt: new Date(2026, 9, 5, 10, 30).toISOString(),
    completedAt: new Date(2026, 9, 5, 10, 31).toISOString(),
    expiresAt: new Date(2026, 9, 12, 10, 30).toISOString(),
    errorCode: null,
    ...overrides,
  };
}

export const ordersExport = reportExport();

export const inventoryExport = reportExport({
  taskId: INVENTORY_EXPORT_ID,
  reportType: 'Inventory',
  parameters: { search: null, lowStockOnly: true, timeZone: 'Asia/Ho_Chi_Minh', language: 'vi' },
  fileName: 'inventory_20261005-0900.xlsx',
  rowCount: 20,
  sizeBytes: 5 * 1024 * 1024 + 512 * 1024,
  createdAt: new Date(2026, 9, 5, 9, 0).toISOString(),
  completedAt: new Date(2026, 9, 5, 9, 0, 5).toISOString(),
  expiresAt: new Date(2026, 9, 12, 9, 0).toISOString(),
});

const unfinished = { rowCount: null, sizeBytes: null, completedAt: null };

export const failedExport = reportExport({
  ...unfinished,
  taskId: FAILED_EXPORT_ID,
  status: 'Failed',
  errorCode: 'storage_unavailable',
  createdAt: new Date(2026, 9, 4, 8, 0).toISOString(),
});

export const expiredExport = reportExport({
  taskId: EXPIRED_EXPORT_ID,
  status: 'Expired',
  createdAt: new Date(2026, 8, 20, 8, 0).toISOString(),
  expiresAt: new Date(2026, 8, 27, 8, 0).toISOString(),
});

export const pendingExport = reportExport({
  ...unfinished,
  taskId: PENDING_EXPORT_ID,
  reportType: 'Inventory',
  status: 'Pending',
  parameters: { search: 'TEA', lowStockOnly: false, timeZone: 'Asia/Ho_Chi_Minh', language: 'vi' },
  createdAt: new Date(2026, 9, 5, 11, 0).toISOString(),
});

/** `GET /api/reports/exports`; records the query string of every request. `answer` can replace the response. */
export function serveExports(initial: ReportExport[] = [], totalCount?: number) {
  const state: { items: ReportExport[]; totalCount?: number; answer?: () => Response } = {
    items: initial,
    totalCount,
  };
  const requests: Record<string, string>[] = [];
  server.use(
    http.get(api('/reports/exports'), ({ request }) => {
      const url = new URL(request.url);
      requests.push(Object.fromEntries(url.searchParams));
      if (state.answer) {
        return state.answer();
      }

      const page: PagedResult<ReportExport> = {
        items: state.items,
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 20),
        totalCount: state.totalCount ?? state.items.length,
      };
      return HttpResponse.json(page);
    }),
  );
  return { state, requests };
}

/** One export request the API received. */
export interface ExportRequestRecord {
  report: 'orders' | 'inventory';
  body: Record<string, unknown>;
  language: string | null;
}

/**
 * The two request endpoints. Every request is recorded; the default answer is the API's 202 with a task id. `answer`
 * replaces it (a problem document); `onAccepted` lets a test add the new export to its list.
 */
export function serveExportRequests() {
  const requests: ExportRequestRecord[] = [];
  const state: {
    answer?: (request: ExportRequestRecord) => Response | Promise<Response>;
    onAccepted?: (taskId: string) => void;
  } = {};
  const handle = (report: ExportRequestRecord['report']) =>
    http.post(api(`/reports/${report}/export`), async ({ request }) => {
      const record: ExportRequestRecord = {
        report,
        body: (await request.json()) as Record<string, unknown>,
        language: request.headers.get('Accept-Language'),
      };
      requests.push(record);
      if (state.answer) {
        return state.answer(record);
      }

      const taskId = `0199a1b2-0000-7000-8000-00000000f${String(requests.length).padStart(3, '0')}`;
      state.onAccepted?.(taskId);
      return HttpResponse.json({ taskId }, { status: 202 });
    });
  server.use(handle('orders'), handle('inventory'));
  return { requests, state };
}

/** The link the API signs for an export: at the storage origin, with a signature that marks it as a credential. */
export function downloadLink(taskId: string): ExportDownload {
  return {
    url: `${STORAGE_ORIGIN}/omnichannel-test/exports/${taskId}.xlsx?X-Amz-Signature=secret-of-${taskId}`,
    expiresAt: new Date(Date.now() + 5 * 60 * 1000).toISOString(),
    fileName: 'orders_20261005-1030.xlsx',
  };
}

/** `GET /api/reports/exports/{taskId}/download`; records the id and the `Authorization` header of every request. */
export function serveDownloads() {
  const requests: { taskId: string; authorization: string | null }[] = [];
  const state: { answer?: (taskId: string) => Response } = {};
  server.use(
    http.get(api('/reports/exports/:taskId/download'), ({ request, params }) => {
      const taskId = String(params.taskId);
      requests.push({ taskId, authorization: request.headers.get('Authorization') });
      return state.answer ? state.answer(taskId) : HttpResponse.json(downloadLink(taskId));
    }),
  );
  return { requests, state };
}
