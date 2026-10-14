import { randomBytes } from 'node:crypto';
import { Api, type ProcessingTask } from './api';
import { required, type RunData } from './env';

/** Letters and digits, an upper-case letter, a lower-case letter and a digit: the API's password policy. */
function newPassword(): string {
  return `E2e${randomBytes(9).toString('hex')}Q9`;
}

interface Created {
  productId: string;
}

/**
 * Prepares one run (task 027 C1): its own Staff and Manager with random passwords, a customer and the products of the
 * specs, all named `E2E027-<run>-...`. Passed to the workers as `E2E_RUN_DATA` (environment of this process only).
 */
export default async function globalSetup(): Promise<void> {
  const runId = `E2E027-${randomBytes(3).toString('hex').toUpperCase()}`;
  const admin = await Api.signIn(required('E2E_ADMIN_EMAIL'), required('E2E_ADMIN_PASSWORD'));
  try {
    const staff = { email: `${runId.toLowerCase()}-staff@example.com`, password: newPassword() };
    const manager = { email: `${runId.toLowerCase()}-manager@example.com`, password: newPassword() };
    const staffUser = await admin.post<{ userId: string }>('/api/users', { ...staff, role: 'Staff' });
    const managerUser = await admin.post<{ userId: string }>('/api/users', { ...manager, role: 'Manager' });

    const customerName = `${runId} customer`;
    const customer = await admin.post<{ customerId: string }>('/api/customers', {
      name: customerName,
      phone: null,
      email: `${runId.toLowerCase()}@example.com`,
      address: null,
    });

    const product = async (suffix: string, threshold: number, stock: number) => {
      const sku = `${runId}-${suffix}`;
      const created = await admin.post<Created>('/api/products', {
        sku,
        name: `${runId} ${suffix}`,
        price: 30000,
        lowStockThreshold: threshold,
      });
      if (stock > 0) {
        await admin.post('/api/inventory/import', {
          documentNumber: `${sku}-GRN`,
          items: [{ productId: created.productId, stock }],
        });
      }
      return { sku, productId: created.productId };
    };

    const order = await product('ORDER', 0, 500);
    const trace = await product('TRACE', 0, 50);
    const alert = await product('ALERT', 5, 6);
    const image = await product('IMAGE', 0, 0);

    // The trace product on pos with SIM-TEMP-: with the simulator's SKU rules on, every sync of it fails its first
    // attempt (channel_unavailable) and succeeds at the second - a failed attempt with its error to trace.
    const channels = await admin.get<{ channelId: string; slug: string }[]>('/api/channels');
    const pos = channels.find((channel) => channel.slug === 'pos');
    if (!pos) {
      throw new Error('The pos channel does not exist.');
    }
    const mapping = await admin.post<{ channelProductId: string; syncTaskId: string }>(
      `/api/channels/${pos.channelId}/products`,
      { productId: trace.productId, externalSku: `SIM-TEMP-${runId}` },
    );
    const initial: ProcessingTask = await admin.waitForTask(mapping.syncTaskId);

    const data: RunData = {
      runId,
      staff,
      manager,
      customerName,
      customerId: customer.customerId,
      orderSku: order.sku,
      traceSku: trace.sku,
      traceProductId: trace.productId,
      alertSku: alert.sku,
      alertProductId: alert.productId,
      imageSku: image.sku,
      imageProductId: image.productId,
      skuRules: initial.attempts.length >= 2,
    };
    process.env.E2E_RUN_DATA = JSON.stringify(data);
    process.env.E2E_RUN_CLEANUP = JSON.stringify({
      users: [staffUser.userId, managerUser.userId],
      mappings: [
        { channelId: pos.channelId, channelProductId: mapping.channelProductId, externalSku: `SIM-TEMP-${runId}` },
      ],
    });
  } finally {
    await admin.dispose();
  }
}
