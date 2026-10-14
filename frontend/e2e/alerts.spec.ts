import type { DashboardSummary } from './support/api';
import { baseUrl } from './support/env';
import { expect, expectAccessible, menuLink, test } from './support/fixtures';

/**
 * Criterion 10 (doc 16): an order takes a product below its threshold; the low-stock alert is on the Alerts page and
 * counted on the dashboard's alert card; a Manager acknowledges it.
 */
test('alerts: a low-stock alert by threshold, on the Alerts page and the dashboard; Manager acknowledges', async ({
  page,
  signIn,
  run,
  staffApi,
  managerApi,
}) => {
  // 6 units, threshold 5: an order of 2 leaves 4.
  const accepted = await staffApi.post<{ taskId: string }>('/api/orders/website', {
    externalOrderId: `${run.runId}-LOW-${Date.now()}`,
    customerId: run.customerId,
    items: [{ productId: run.alertProductId, stock: 2 }],
  });
  expect((await staffApi.waitForTask(accepted.taskId)).status).toBe('Succeeded');

  await signIn('Manager');
  const summary = await managerApi.get<DashboardSummary>('/api/dashboard/summary');
  const alertCard = page.getByTestId('dashboard-alerts');
  const openFigure = alertCard
    .locator('.ant-statistic')
    .filter({ has: page.locator('.ant-statistic-title', { hasText: /^Open$/ }) });
  await expect(openFigure).toContainText(new Intl.NumberFormat('vi-VN').format(summary.alerts.open));
  expect(summary.alerts.open).toBeGreaterThan(0);

  await menuLink(page, 'Alerts').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Alerts' })).toBeVisible();
  const row = page.locator('tbody tr').filter({ hasText: run.alertSku }).filter({ hasText: 'Low stock' });
  await expect(row).toHaveCount(1);
  await expect(row).toContainText('Open');
  await expect(row).toContainText('Available 4 / threshold 5');
  await expectAccessible(page, 'Alerts');

  await row.getByRole('button', { name: 'Acknowledge' }).click();
  const dialog = page.getByRole('dialog').filter({ hasText: 'Acknowledge this alert?' });
  await expect(dialog).toBeVisible();
  await expectAccessible(page, 'Acknowledge dialog');
  await dialog.getByRole('button', { name: 'Acknowledge' }).click();
  await expect(page.getByText('Alert acknowledged.')).toBeVisible();
  await expect(row).toContainText('Acknowledged');
  await expect(row).toContainText(run.manager.email);

  // The dashboard card counts it as acknowledged now.
  await page.goto(`${baseUrl()}/`);
  const now = await managerApi.get<DashboardSummary>('/api/dashboard/summary');
  await expect(
    alertCard
      .locator('.ant-statistic')
      .filter({ has: page.locator('.ant-statistic-title', { hasText: /^Acknowledged$/ }) }),
  ).toContainText(new Intl.NumberFormat('vi-VN').format(now.alerts.acknowledged));
});
