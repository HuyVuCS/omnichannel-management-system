import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import type { Page } from '@playwright/test';
import type { DashboardSummary } from './support/api';
import { required, stackControl } from './support/env';
import { expect, expectAccessible, test } from './support/fixtures';

const numberFormat = new Intl.NumberFormat('vi-VN');

/** The "Orders" figure of the dashboard's orders card. */
function ordersFigure(page: Page) {
  return page
    .getByTestId('dashboard-orders')
    .locator('.ant-statistic')
    .filter({ has: page.locator('.ant-statistic-title', { hasText: /^Orders$/ }) })
    .locator('.ant-statistic-content');
}

/** `docker compose restart api` of the throwaway acceptance stack (E2E_STACK_CONTROL=1 only). */
async function restartApi(): Promise<void> {
  const repository = fileURLToPath(new URL('../../', import.meta.url));
  await promisify(execFile)(
    'docker',
    [
      'compose',
      '-p',
      required('E2E_COMPOSE_PROJECT'),
      '--env-file',
      required('E2E_COMPOSE_ENV_FILE'),
      '-f',
      'docker/docker-compose.prod.yml',
      '-f',
      'docker/docker-compose.acceptance.yml',
      'restart',
      'api',
    ],
    { cwd: repository },
  );
}

/**
 * Criterion 8 (doc 16): the dashboard follows an order created elsewhere without a reload, within 5 s; with stack
 * control, a restart of the API is shown (reconnecting -> live) and the figures are read again from the API (resync).
 */
test('realtime: the dashboard changes by itself within 5 s, and resyncs after the API restarts', async ({
  page,
  signIn,
  run,
  staffApi,
  managerApi,
}) => {
  let navigations = 0;
  page.on('framenavigated', (frame) => {
    if (frame === page.mainFrame()) {
      navigations++;
    }
  });
  await signIn('Manager');
  await expect(page.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeVisible();
  await expect(page.getByTestId('connection-indicator')).toHaveAttribute('data-state', 'connected');
  const before = await managerApi.get<DashboardSummary>('/api/dashboard/summary');
  await expect(ordersFigure(page)).toHaveText(numberFormat.format(before.orders.total));
  await expectAccessible(page, 'Dashboard');
  const navigationsBefore = navigations;

  // Another client (the Staff API session, not this page) creates an order.
  const products = await staffApi.get<{ items: { productId: string }[] }>(
    `/api/products?search=${encodeURIComponent(run.orderSku)}`,
  );
  const accepted = await staffApi.post<{ taskId: string }>('/api/orders/pos', {
    externalOrderId: `${run.runId}-RT-${Date.now()}`,
    customerId: run.customerId,
    items: [{ productId: products.items[0]?.productId, stock: 1 }],
  });
  const task = await staffApi.waitForTask(accepted.taskId);
  expect(task.status).toBe('Succeeded');
  const after = await managerApi.get<DashboardSummary>('/api/dashboard/summary');
  expect(after.orders.total).toBeGreaterThan(before.orders.total);
  // Within 5 s of the end of the task (the task's own time; API and test run on one machine), without a navigation.
  const left = Date.parse(task.updatedAt ?? '') + 5_000 - Date.now();
  expect(left, 'time left of the 5 s after the end of the task').toBeGreaterThan(0);
  await expect(ordersFigure(page)).toHaveText(numberFormat.format(after.orders.total), { timeout: left });
  expect(navigations).toBe(navigationsBefore);
  await expect(page.getByTestId('connection-indicator')).toHaveAttribute('data-state', 'connected');

  if (!stackControl()) {
    test.info().annotations.push({ type: 'skipped', description: 'API restart: E2E_STACK_CONTROL is not 1' });
    return;
  }

  // Watched while it happens: the page notices the lost connection before the API is back.
  const restarting = restartApi();
  await expect(page.getByTestId('connection-indicator')).not.toHaveAttribute('data-state', 'connected', {
    timeout: 60_000,
  });
  await restarting;
  await expect(page.getByTestId('connection-indicator')).toHaveAttribute('data-state', 'connected', {
    timeout: 90_000,
  });
  const fresh = await managerApi.get<DashboardSummary>('/api/dashboard/summary');
  await expect(ordersFigure(page)).toHaveText(numberFormat.format(fresh.orders.total));
  expect(navigations).toBe(navigationsBefore);
});
