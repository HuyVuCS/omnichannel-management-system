import { expect, expectAccessible, menuLink, selectOption, test } from './support/fixtures';

const PANELS = [
  { slug: 'website', title: 'Website' },
  { slug: 'pos', title: 'POS' },
  { slug: 'digital-platform', title: 'Digital Platform Online' },
] as const;

/**
 * Criterion 1 (doc 16): Staff enters an order in each of the three panels; each shows "Reserved" - by push, the
 * realtime connection being live - and the three orders are in the Orders list.
 */
test('order entry: one order per channel panel, each reserved by push, all three in the Orders list', async ({
  page,
  signIn,
  run,
}) => {
  const frames: string[] = [];
  page.on('websocket', (socket) => socket.on('framereceived', (frame) => frames.push(String(frame.payload))));
  await signIn('Staff');
  await menuLink(page, 'Order entry').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Order entry' })).toBeVisible();
  // Live: the outcome of an order follows the hints, nothing polls (OrderOutcome).
  await expect(page.getByTestId('connection-indicator')).toHaveAttribute('data-state', 'connected');
  await expectAccessible(page, 'Order entry');

  const externalIds: string[] = [];
  for (const { slug } of PANELS) {
    const panel = page.getByTestId(`order-panel-${slug}`);
    await panel.getByLabel('Customer').fill(run.customerName);
    await selectOption(page, run.customerName).click();
    await panel.getByLabel('Product, line 1').fill(run.orderSku);
    await selectOption(page, run.orderSku).click();
    await panel.getByLabel('Quantity, line 1').fill('1');

    const submitted = page.waitForResponse(
      (response) => response.url().endsWith(`/api/orders/${slug}`) && response.request().method() === 'POST',
    );
    await panel.getByRole('button', { name: 'Create order' }).click();
    const response = await submitted;
    expect(response.status()).toBe(202);
    const { taskId } = (await response.json()) as { taskId: string };
    externalIds.push((response.request().postDataJSON() as { externalOrderId: string }).externalOrderId);

    const outcome = page.getByTestId(`order-outcome-${slug}`);
    await expect(outcome.getByText('Stock was reserved for the order.')).toBeVisible();
    // The hint that finished the task came over the WebSocket.
    expect(frames.some((frame) => frame.includes(taskId) && frame.includes('"status":"Succeeded"'))).toBe(true);
    await expect(page.getByTestId('connection-indicator')).toHaveAttribute('data-state', 'connected');
  }
  await expectAccessible(page, 'Order entry with three outcomes');

  await menuLink(page, 'Orders').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Orders' })).toBeVisible();
  for (const externalId of externalIds) {
    await expect(page.getByRole('cell', { name: externalId })).toBeVisible();
  }
  await expectAccessible(page, 'Orders');
});
