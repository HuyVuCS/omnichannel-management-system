import { baseUrl } from './support/env';
import { expect, expectAccessible, test } from './support/fixtures';

/**
 * Criterion 14 (doc 16), in the browser: Staff has no Users menu and gets the forbidden page at /users; signing out
 * leads to the login page; a protected address while signed out leads there too.
 */
test('access: Staff has no Users page; sign-out; a protected address needs a session', async ({ page, signIn }) => {
  await signIn('Staff');
  const sider = page.getByTestId('app-sider');
  await expect(sider.getByRole('link', { name: 'Orders', exact: true })).toBeVisible();
  await expect(sider.getByRole('link', { name: 'Users', exact: true })).toHaveCount(0);
  await expectAccessible(page, 'Dashboard (Staff)');

  await page.goto(`${baseUrl()}/users`);
  await expect(page.getByRole('heading', { level: 1, name: 'No access' })).toBeVisible();
  await expectAccessible(page, 'Forbidden page');

  await page.getByTestId('app-header').getByRole('button', { name: 'Log out' }).click();
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
  await expectAccessible(page, 'Login');

  await page.goto(`${baseUrl()}/orders`);
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByLabel('E-mail')).toBeVisible();
});
