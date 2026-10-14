import { baseUrl } from './support/env';
import { expect, expectAccessible, test } from './support/fixtures';

interface TaskPage {
  items: { taskId: string; status: string }[];
}

/**
 * Criterion 9 (doc 16): from an order's detail to its task and attempts (worker, instance, attempt number), to the
 * stock-sync task of the same flow with the error of its failed attempt (the SIM-TEMP- product: with the simulator's
 * SKU rules on, its first attempt fails with channel_unavailable), and the Logs page filtered by a flow's correlation id.
 */
test('trace: order -> task -> attempts -> failed attempt of the flow -> logs of the flow', async ({
  page,
  signIn,
  run,
  staffApi,
}) => {
  // The flow: an order of the SIM-TEMP- product (synced to pos), and an order that cannot be reserved.
  const correlationId = `${run.runId}-trace-${Date.now()}`;
  const shortCorrelationId = `${correlationId}-short`;
  const accepted = await staffApi.post<{ taskId: string }>(
    '/api/orders/website',
    {
      externalOrderId: `${run.runId}-TRACE-${Date.now()}`,
      customerId: run.customerId,
      items: [{ productId: run.traceProductId, stock: 1 }],
    },
    { 'X-Correlation-Id': correlationId },
  );
  const orderTask = await staffApi.waitForTask(accepted.taskId);
  expect(orderTask.status).toBe('Succeeded');
  const short = await staffApi.post<{ taskId: string }>(
    '/api/orders/pos',
    {
      externalOrderId: `${run.runId}-SHORT-${Date.now()}`,
      customerId: run.customerId,
      items: [{ productId: run.traceProductId, stock: 10_000 }],
    },
    { 'X-Correlation-Id': shortCorrelationId },
  );
  const shortTask = await staffApi.waitForTask(short.taskId);
  // The stock sync the order started on pos, finished.
  await expect
    .poll(
      async () => {
        const page = await staffApi.get<TaskPage>(
          `/api/processing/tasks?taskType=StockSync&correlationId=${encodeURIComponent(correlationId)}`,
        );
        return (
          page.items.length > 0 && page.items.every((task) => task.status === 'Succeeded' || task.status === 'Failed')
        );
      },
      { timeout: 90_000 },
    )
    .toBe(true);

  await signIn('Manager');

  // 1. The order's detail: its task with the attempt of the Order Worker.
  await page.goto(`${baseUrl()}/orders/${orderTask.orderId}`);
  const taskBlock = page.getByTestId(`order-task-${orderTask.taskId}`);
  await expect(taskBlock.getByText('Order processing')).toBeVisible();
  await expect(taskBlock.getByText(correlationId)).toBeVisible();
  const attemptRow = taskBlock.locator('tbody tr').filter({ hasText: 'OrderWorker' });
  await expect(attemptRow).toHaveCount(1);
  await expect(attemptRow.locator('td').first()).toHaveText('1');
  await expect(attemptRow).toContainText(orderTask.attempts[0]?.workerInstanceId ?? 'no instance');
  await expectAccessible(page, 'Order detail');

  // 2. The tasks of the flow, by its correlation id, and the stock-sync task's attempts.
  await page.goto(`${baseUrl()}/processing?correlationId=${encodeURIComponent(correlationId)}`);
  await expect(page.getByRole('heading', { level: 1, name: 'Sync & processing' })).toBeVisible();
  const syncRow = page.locator('tbody tr').filter({ hasText: 'Stock sync' }).first();
  await expect(syncRow).toContainText(run.traceSku);
  await expectAccessible(page, 'Sync & processing filtered by correlation id');
  await syncRow.getByRole('button', { name: 'Details' }).click();
  const attempts = page.getByTestId('task-attempts');
  await expect(attempts.locator('tbody tr').filter({ hasText: 'StockSyncWorker' }).first()).toBeVisible();
  if (run.skuRules) {
    const failed = attempts.locator('tbody tr').filter({ hasText: 'channel_unavailable' });
    await expect(failed).toHaveCount(1);
    await expect(failed.locator('td').first()).toHaveText('1');
    await expect(failed).toContainText('Failed');
    await expect(failed).toContainText('unavailable');
    await expect(attempts.locator('tbody tr').filter({ hasText: 'Succeeded' })).toHaveCount(1);
  } else {
    test.info().annotations.push({ type: 'skipped', description: 'failed attempt: SKU rules off' });
  }
  await expectAccessible(page, 'Processing task detail');

  // 3. The order that could not be reserved: its detail, then every event of its flow on the Logs page.
  await page.goto(`${baseUrl()}/orders/${shortTask.orderId}`);
  await expect(page.getByTestId(`order-task-${short.taskId}`).getByText(shortCorrelationId)).toBeVisible();
  await page.goto(`${baseUrl()}/logs?correlationId=${encodeURIComponent(shortCorrelationId)}`);
  await expect(page.getByRole('heading', { level: 1, name: 'Logs / audit' })).toBeVisible();
  await expect(page.locator('tbody tr').filter({ hasText: 'Order short of stock' })).toHaveCount(1);
  await expectAccessible(page, 'Logs filtered by correlation id');
});
