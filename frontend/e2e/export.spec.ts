import { readFile } from 'node:fs/promises';
import { expect, expectAccessible, menuLink, test } from './support/fixtures';

/**
 * Criterion 12 (doc 16): a Manager requests an inventory export on Reports; its status becomes "Succeeded" without a
 * reload (push); the download is an .xlsx file (ZIP signature "PK").
 */
test('export: inventory export requested, Succeeded live, downloaded as an .xlsx file', async ({
  page,
  signIn,
  run,
}) => {
  await signIn('Manager');
  await menuLink(page, 'Reports').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Reports & Excel export' })).toBeVisible();
  await expectAccessible(page, 'Reports');

  // The report switch is a Segmented control: its radio inputs are hidden, the label is what is clicked.
  await page.locator('.ant-segmented-item', { hasText: 'Inventory' }).click();
  await expect(page.getByRole('radio', { name: 'Inventory' })).toBeChecked();
  const form = page.getByTestId('inventory-export-form');
  await form.getByLabel('Search products').fill(run.alertSku);
  const requested = page.waitForResponse(
    (response) => response.url().endsWith('/api/reports/inventory/export') && response.request().method() === 'POST',
  );
  await form.getByRole('button', { name: 'Request export' }).click();
  const response = await requested;
  expect(response.status()).toBe(202);
  const { taskId } = (await response.json()) as { taskId: string };
  await expect(page.getByText('Export requested - it appears below when it is ready.')).toBeVisible();

  const row = page.locator(`tbody tr[data-row-key="${taskId}"]`);
  await expect(row).toContainText('Succeeded', { timeout: 60_000 });
  await expectAccessible(page, 'Reports with a finished export');

  const download = page.waitForEvent('download');
  await row.getByRole('button', { name: 'Download' }).click();
  const file = await (await download).path();
  const bytes = await readFile(file);
  expect(bytes.subarray(0, 2).toString('latin1')).toBe('PK');
});
