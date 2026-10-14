import { expect, expectAccessible, menuLink, test } from './support/fixtures';

/** A 2 x 2 PNG. */
const PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR42mNk+M9Qz8DAwMDAxMDAAAAeVQKBy1Xb+gAAAABJRU5ErkJggg==',
  'base64',
);

/**
 * Criterion 13 (doc 16): a Manager uploads a product image in the product dialog (presign, upload to the storage,
 * confirm); the thumbnail loads from the storage.
 */
test('image: uploaded in the product dialog, the thumbnail loads', async ({ page, signIn, run }) => {
  await signIn('Manager');
  await menuLink(page, 'Products').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Products' })).toBeVisible();
  await page.getByLabel('Search by SKU or name').fill(run.imageSku);
  const row = page.locator('tbody tr').filter({ hasText: run.imageSku });
  await expect(row).toHaveCount(1);
  await row.getByRole('button', { name: 'Edit' }).click();
  const dialog = page.getByRole('dialog').filter({ hasText: 'Edit product' });
  await expect(dialog).toBeVisible();
  await expectAccessible(page, 'Edit product dialog');

  await dialog
    .getByTestId('product-image-input')
    .setInputFiles({ name: 'e2e.png', mimeType: 'image/png', buffer: PNG });
  await expect(page.getByText('Image saved.')).toBeVisible();
  const thumbnail = dialog.getByTestId('product-image-field').locator('img');
  await expect(thumbnail).toBeVisible();
  await expect.poll(() => thumbnail.evaluate((image) => (image as HTMLImageElement).naturalWidth)).toBeGreaterThan(0);
  await expectAccessible(page, 'Edit product dialog with an image');
});
