import { http, HttpResponse } from 'msw';
import type { DashboardSummary } from '@/features/dashboard/dashboardApi';
import { api, server } from './server';

export const WEBSITE_ID = '0199a1b2-0000-7000-8000-00000000c001';
export const POS_ID = '0199a1b2-0000-7000-8000-00000000c002';
export const MARKETPLACE_ID = '0199a1b2-0000-7000-8000-00000000c003';

/** When the fixture summary was generated (local time 08:05:09). */
export const GENERATED_AT = new Date(2026, 9, 2, 8, 5, 9);

/** A summary as `GET /api/dashboard/summary` returns it: every status, task type and channel, zeros included. */
export function summary(overrides: Partial<DashboardSummary> = {}): DashboardSummary {
  return {
    generatedAt: GENERATED_AT.toISOString(),
    from: new Date(2026, 9, 1, 8, 5, 9).toISOString(),
    to: GENERATED_AT.toISOString(),
    orders: {
      total: 1234,
      byStatus: [
        { status: 'Pending', count: 0 },
        { status: 'Reserved', count: 1200 },
        { status: 'InsufficientStock', count: 30 },
        { status: 'Cancelled', count: 0 },
        { status: 'Completed', count: 4 },
      ],
      byChannel: [
        {
          channelId: MARKETPLACE_ID,
          slug: 'digital-platform',
          name: 'Digital Platform Online',
          total: 0,
          reserved: 0,
          insufficientStock: 0,
        },
        { channelId: POS_ID, slug: 'pos', name: 'POS', total: 34, reserved: 30, insufficientStock: 4 },
        { channelId: WEBSITE_ID, slug: 'website', name: 'Website', total: 1200, reserved: 1170, insufficientStock: 26 },
      ],
      salesAmount: 98765432,
    },
    inventory: { products: 57, lowStock: 6, outOfStock: 2 },
    processing: [
      { taskType: 'OrderProcessing', pending: 3, inProgress: 1, retryScheduled: 0, failed: 2, succeeded: 1230 },
      { taskType: 'StockSync', pending: 0, inProgress: 0, retryScheduled: 5, failed: 7, succeeded: 2468 },
      { taskType: 'Invoice', pending: 0, inProgress: 0, retryScheduled: 0, failed: 0, succeeded: 0 },
      { taskType: 'Email', pending: 0, inProgress: 0, retryScheduled: 0, failed: 0, succeeded: 0 },
    ],
    stockSync: [
      {
        channelId: MARKETPLACE_ID,
        slug: 'digital-platform',
        name: 'Digital Platform Online',
        retryScheduled: 0,
        failed: 0,
        lastSucceededAt: null,
      },
      {
        channelId: POS_ID,
        slug: 'pos',
        name: 'POS',
        retryScheduled: 5,
        failed: 7,
        lastSucceededAt: new Date(2026, 9, 2, 5, 5, 9).toISOString(),
      },
      {
        channelId: WEBSITE_ID,
        slug: 'website',
        name: 'Website',
        retryScheduled: 0,
        failed: 0,
        lastSucceededAt: new Date(2026, 9, 2, 8, 3, 9).toISOString(),
      },
    ],
    outbox: { pending: 3, failed: 1, oldestPendingAt: new Date(2026, 9, 2, 8, 0, 9).toISOString() },
    ...overrides,
  };
}

/** `GET /api/dashboard/summary`; records the query string of every request. `answer` can replace the response. */
export function serveSummary(initial: DashboardSummary = summary()) {
  const state: { summary: DashboardSummary; answer?: () => Response } = { summary: initial };
  const requests: Record<string, string>[] = [];
  server.use(
    http.get(api('/dashboard/summary'), ({ request }) => {
      requests.push(Object.fromEntries(new URL(request.url).searchParams));
      return state.answer ? state.answer() : HttpResponse.json(state.summary);
    }),
  );
  return { state, requests };
}
