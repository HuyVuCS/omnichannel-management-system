import { http, HttpResponse } from 'msw';
import type { Channel } from '@/features/channels/channelsApi';
import type { OrderDetail, OrderSummary } from '@/features/orders/ordersApi';
import type { ProcessingAttempt, ProcessingTaskDetail } from '@/features/processing/processingApi';
import { AN_ID, COFFEE_ID, TEA_ID } from './catalog';
import { api, server } from './server';

export const ORDER_ID = '0199a1b2-0000-7000-8000-000000000001';
export const TASK_ID = '0199a1b2-0000-7000-8000-0000000000aa';

export function channel(overrides: Partial<Channel> & Pick<Channel, 'slug' | 'name'>): Channel {
  return {
    channelId: `channel-${overrides.slug}`,
    type: 'Website',
    status: 'Active',
    createdAt: '2026-01-01T00:00:00+00:00',
    updatedAt: null,
    ...overrides,
  };
}

/** The three seeded channels, as the development database has them. */
export const seededChannels: Channel[] = [
  channel({ slug: 'digital-platform', name: 'Digital Platform Online', type: 'DigitalPlatform' }),
  channel({ slug: 'website', name: 'Website', type: 'Website' }),
  channel({ slug: 'pos', name: 'POS', type: 'Pos' }),
];

/** `GET /api/channels`; counts the requests. */
export function serveChannels(channels: Channel[] = seededChannels) {
  const requests = { count: 0 };
  server.use(
    http.get(api('/channels'), () => {
      requests.count += 1;
      return HttpResponse.json(channels);
    }),
  );
  return requests;
}

export function attempt(overrides: Partial<ProcessingAttempt> = {}): ProcessingAttempt {
  return {
    attemptNo: 1,
    status: 'Succeeded',
    workerName: 'order-worker',
    workerInstanceId: 'order-worker@host-1:4242',
    errorCode: null,
    errorMessage: null,
    startedAt: new Date(2026, 9, 1, 8, 5, 1).toISOString(),
    finishedAt: new Date(2026, 9, 1, 8, 5, 2).toISOString(),
    ...overrides,
  };
}

/** A processing task as `GET /api/processing/tasks/{id}` returns it. */
export function task(overrides: Partial<ProcessingTaskDetail> = {}): ProcessingTaskDetail {
  return {
    taskId: TASK_ID,
    taskType: 'OrderProcessing',
    status: 'Pending',
    orderId: null,
    channelId: null,
    productId: null,
    productSku: null,
    retryCount: 0,
    attemptBudgetStart: 0,
    correlationId: 'corr-task-1',
    messageId: 'message-1',
    createdAt: new Date(2026, 9, 1, 8, 5).toISOString(),
    updatedAt: null,
    attempts: [],
    message: {
      outboxEventId: 'message-1',
      eventType: 'order.created',
      status: 'Published',
      createdAt: new Date(2026, 9, 1, 8, 5).toISOString(),
      publishedAt: new Date(2026, 9, 1, 8, 5).toISOString(),
      publishAttempts: 1,
      lastError: null,
    },
    ...overrides,
  };
}

export function orderSummary(overrides: Partial<OrderSummary> = {}): OrderSummary {
  return {
    orderId: ORDER_ID,
    externalOrderId: 'WEB-20261001-ABC123',
    status: 'Reserved',
    totalAmount: 1234567,
    itemCount: 2,
    channel: { channelId: 'channel-website', slug: 'website', name: 'Website' },
    customer: { customerId: AN_ID, name: 'Nguyễn Văn An' },
    createdAt: new Date(2026, 9, 1, 8, 5).toISOString(),
    ...overrides,
  };
}

/** An order as `GET /api/orders/{id}` returns it: two lines and the task that created it, with one attempt. */
export function orderDetail(overrides: Partial<OrderDetail> = {}): OrderDetail {
  return {
    ...orderSummary(),
    items: [
      { productId: COFFEE_ID, sku: 'COF-01', productName: 'Cà phê', stock: 2, unitPrice: 100000, lineTotal: 234567 },
      { productId: TEA_ID, sku: 'TEA-01', productName: 'Trà xanh', stock: 100, unitPrice: 10000, lineTotal: 1000000 },
    ],
    tasks: [
      {
        taskId: TASK_ID,
        taskType: 'OrderProcessing',
        status: 'Succeeded',
        retryCount: 0,
        correlationId: 'corr-task-1',
        messageId: 'message-1',
        createdAt: new Date(2026, 9, 1, 8, 5).toISOString(),
        updatedAt: new Date(2026, 9, 1, 8, 5, 2).toISOString(),
        attempts: [attempt()],
      },
    ],
    ...overrides,
  };
}
