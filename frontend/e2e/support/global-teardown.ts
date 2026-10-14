import { Api } from './api';
import { required } from './env';

interface Cleanup {
  users: string[];
  mappings: { channelId: string; channelProductId: string; externalSku: string }[];
}

/** The run's accounts are locked and its mappings deactivated (products and orders stay: demo data, no delete API). */
export default async function globalTeardown(): Promise<void> {
  const cleanup = process.env.E2E_RUN_CLEANUP;
  if (!cleanup) {
    return;
  }

  const { users, mappings } = JSON.parse(cleanup) as Cleanup;
  const admin = await Api.signIn(required('E2E_ADMIN_EMAIL'), required('E2E_ADMIN_PASSWORD'));
  try {
    for (const mapping of mappings) {
      await admin.status('PUT', `/api/channels/${mapping.channelId}/products/${mapping.channelProductId}`, {
        externalSku: mapping.externalSku,
        status: 'Inactive',
      });
    }
    for (const userId of users) {
      await admin.status('POST', `/api/users/${userId}/lock`);
    }
  } finally {
    await admin.dispose();
  }
}
