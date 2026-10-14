/**
 * Configuration of the end-to-end suite (task 027 Part C), from the environment only - never from a file of the
 * repository, and never printed:
 * - `E2E_BASE_URL`        the frontend (e.g. `vite preview` on http://localhost:4173, or the static host);
 * - `E2E_API_URL`         the API origin the frontend was built for (VITE_API_BASE_URL);
 * - `E2E_ADMIN_EMAIL` / `E2E_ADMIN_PASSWORD`  an Admin of that stack (the suite creates its own Staff and Manager);
 * - `E2E_STACK_CONTROL=1` (throwaway stacks only) the realtime spec restarts the API container
 *   (`E2E_COMPOSE_PROJECT`, `E2E_COMPOSE_ENV_FILE`; the compose files of the acceptance stack).
 * Values the global setup creates for the specs (accounts, data) are passed on as `E2E_RUN_*` variables.
 */
export function required(name: string): string {
  const value = process.env[name];
  if (!value) {
    throw new Error(`${name} is not set (see frontend/e2e/support/env.ts).`);
  }
  return value;
}

export const baseUrl = (): string => required('E2E_BASE_URL').replace(/\/$/, '');
export const apiUrl = (): string => required('E2E_API_URL').replace(/\/$/, '');
export const stackControl = (): boolean => process.env.E2E_STACK_CONTROL === '1';

/** What the global setup prepared (see `global-setup.ts`). */
export interface RunData {
  runId: string;
  staff: { email: string; password: string };
  manager: { email: string; password: string };
  customerName: string;
  /** Product with stock for the order-entry and realtime specs. */
  orderSku: string;
  /** Product mapped on pos with a SIM-TEMP- external SKU (a failed first attempt when the SKU rules are on). */
  traceSku: string;
  traceProductId: string;
  /** Product with threshold 5 and 6 units: low stock after an order of 2. */
  alertSku: string;
  alertProductId: string;
  /** Product without a stock-sync mapping for the image spec. */
  imageSku: string;
  imageProductId: string;
  customerId: string;
  skuRules: boolean;
}

export function runData(): RunData {
  return JSON.parse(required('E2E_RUN_DATA')) as RunData;
}
