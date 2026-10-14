import type { Locator, Page } from '@playwright/test';
import { expect, expectAccessible, menuLink, settled, test } from './support/fixtures';

const MENU = [
  ['Dashboard', 'Dashboard'],
  ['Order entry', 'Order entry'],
  ['Orders', 'Orders'],
  ['Inventory', 'Inventory'],
  ['Inventory movements', 'Inventory movements'],
  ['Products', 'Products'],
  ['Customers', 'Customers'],
  ['Channels', 'Channels'],
  ['Sync & processing', 'Sync & processing'],
  ['Alerts', 'Alerts'],
  ['Logs / audit', 'Logs / audit'],
  ['Reports', 'Reports & Excel export'],
  ['Users', 'Users'],
] as const;

/** The focus ring of task 027 A3: 2 px solid #0958d9, 2 px away from the element (inside a header cell). */
const RING = { style: 'solid', width: '2px', color: 'rgb(9, 88, 217)', offset: '2px' };
const HEADER_RING = { ...RING, offset: '-2px' };

/**
 * Focuses an element as the keyboard does (a key press first, so the browser shows focus as it would after Tab) and
 * reads the outline it draws.
 */
async function focusRing(page: Page, element: Locator) {
  await page.keyboard.press('Shift');
  await element.focus();
  await expect(element).toBeFocused();
  // AntD animates its controls' styles (`transition: all`): the outline is read once the transition is over.
  await settled(page);
  return element.evaluate((node) => {
    // An input inside one of AntD's field boxes: the ring is drawn around the box (src/index.css).
    const ringed = node.closest('.ant-input-affix-wrapper, .ant-select, .ant-input-number, .ant-picker') ?? node;
    const style = getComputedStyle(ringed);
    return {
      focusVisible: node.matches(':focus-visible'),
      style: style.outlineStyle,
      width: style.outlineWidth,
      color: style.outlineColor,
      offset: style.outlineOffset,
    };
  });
}

/** Task 027 C4: every menu page once, signed in as Admin, through axe in the browser; and the focus ring (A3). */
test('every menu page passes axe in the browser; focus is visible', async ({ page, signIn }) => {
  await signIn('Admin');
  for (const [link, heading] of MENU) {
    await menuLink(page, link).click();
    await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible();
    await expectAccessible(page, heading);
  }

  await menuLink(page, 'Products').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Products' })).toBeVisible();
  await settled(page);
  const targets: [string, Locator, typeof RING][] = [
    ['a button', page.getByRole('button', { name: 'New product' }), RING],
    ['a link', menuLink(page, 'Orders'), RING],
    ['a menu item', page.getByTestId('app-sider').getByRole('menuitem').first(), RING],
    ['an input', page.getByLabel('Search by SKU or name'), RING],
    ['a sortable column header', page.locator('th.ant-table-column-has-sorters').first(), HEADER_RING],
  ];
  for (const [what, element, expected] of targets) {
    const ring = await focusRing(page, element);
    expect(ring, `focus ring of ${what}`).toEqual({ focusVisible: true, ...expected });
  }
});
