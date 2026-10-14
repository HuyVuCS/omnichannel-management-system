import { request, type APIRequestContext, type APIResponse } from '@playwright/test';
import { apiUrl } from './env';

/**
 * The API as the specs and the global setup use it outside the browser: to prepare data and to check what the UI shows.
 * A token is held in memory only; nothing here is logged.
 */
export class Api {
  private readonly context: APIRequestContext;
  private readonly token: string;

  private constructor(context: APIRequestContext, token: string) {
    this.context = context;
    this.token = token;
  }

  /**
   * Signs in. The login endpoint allows ten requests a minute per address (RateLimitPolicies): a 429 is that limit, not
   * the answer under test - the minute is waited out, twice at most (as `scripts/smoke/_hardening.ps1`).
   */
  static async signIn(email: string, password: string): Promise<Api> {
    const context = await request.newContext({ baseURL: apiUrl() });
    for (let attempt = 0; ; attempt++) {
      const response = await context.post('/api/auth/login', { data: { email, password } });
      if (response.status() === 429 && attempt < 2) {
        await new Promise((resolve) => setTimeout(resolve, 61_000));
        continue;
      }
      if (response.status() !== 200) {
        throw new Error(`Sign-in of a test account failed (HTTP ${response.status()}).`);
      }
      const { accessToken } = (await response.json()) as { accessToken: string };
      return new Api(context, accessToken);
    }
  }

  async dispose(): Promise<void> {
    await this.context.dispose();
  }

  private headers(extra?: Record<string, string>) {
    return { Authorization: `Bearer ${this.token}`, ...extra };
  }

  async get<T>(path: string): Promise<T> {
    return (await this.expectOk(await this.context.get(path, { headers: this.headers() }))).json() as Promise<T>;
  }

  async post<T>(path: string, data?: unknown, headers?: Record<string, string>): Promise<T> {
    const response = await this.context.post(path, { headers: this.headers(headers), data });
    return (await this.expectOk(response)).json() as Promise<T>;
  }

  async put<T>(path: string, data?: unknown): Promise<T> {
    return (await this.expectOk(await this.context.put(path, { headers: this.headers(), data }))).json() as Promise<T>;
  }

  /** The status of a request whose answer is not needed (cleanup). */
  async status(method: 'POST' | 'PUT', path: string, data?: unknown): Promise<number> {
    const response = await this.context.fetch(path, { method, headers: this.headers(), data });
    return response.status();
  }

  private async expectOk(response: APIResponse): Promise<APIResponse> {
    if (!response.ok()) {
      throw new Error(`${response.url().replace(apiUrl(), '')} answered HTTP ${response.status()}.`);
    }
    return response;
  }

  /** Polls a processing task until it is Succeeded or Failed. */
  async waitForTask(taskId: string, seconds = 90): Promise<ProcessingTask> {
    const deadline = Date.now() + seconds * 1000;
    for (;;) {
      const task = await this.get<ProcessingTask>(`/api/processing/tasks/${taskId}`);
      if (task.status === 'Succeeded' || task.status === 'Failed' || Date.now() > deadline) {
        return task;
      }
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }
  }
}

export interface ProcessingTask {
  taskId: string;
  taskType: string;
  status: string;
  orderId: string | null;
  correlationId: string;
  updatedAt: string | null;
  attempts: {
    attemptNo: number;
    status: string;
    workerName: string;
    workerInstanceId: string;
    errorCode: string | null;
  }[];
}

export interface DashboardSummary {
  orders: { total: number };
  alerts: { open: number; acknowledged: number };
}
