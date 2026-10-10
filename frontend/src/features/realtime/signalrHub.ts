// Types only: they are erased, so the library itself stays behind the dynamic import below.
import type { HttpRequest, HttpResponse, ILogger } from '@microsoft/signalr';
import { apiUrl } from '@/shared/api/apiOrigin';
import { HubUnauthorizedError, type HintBatch, type HubFactory } from './hub';

/**
 * On the origin of the API: the page's own by default (the dev server proxies `/api`, WebSockets included), or the one
 * the build names (`VITE_API_BASE_URL`).
 */
export function hubUrl(): string {
  return apiUrl('/api/hubs/notifications');
}

/**
 * The access token travels in the query string of the WebSocket request (browsers cannot set headers there), so no
 * text of the SignalR client may reach the console with it.
 */
export function redactAccessToken(text: string): string {
  return text.replace(/access_token=[^&\s'"]*/g, 'access_token=***');
}

/**
 * The real hub: the official SignalR client, loaded here with a dynamic `import()` so it is a chunk of its own that
 * the login page never downloads.
 *
 * - Logging: warnings and errors only, with the access token removed. The client's Information level prints the
 *   connection URL.
 * - No automatic reconnect of the library: `realtimeConnection.ts` owns the retries, because the library neither
 *   retries a failed first start nor tells a refused token from an unreachable server.
 * - A refused token (401 on negotiate) is reported as {@link HubUnauthorizedError}. The library hides the status
 *   behind a generic negotiation error, so the status of its HTTP requests is recorded by the client passed to it.
 */
export const createSignalrHub: HubFactory = async ({ accessToken, onHints, onClose }) => {
  const signalr = await import('@microsoft/signalr');

  const logger: ILogger = {
    log(level, message) {
      if (level >= signalr.LogLevel.Error) {
        console.error(redactAccessToken(message));
      } else if (level >= signalr.LogLevel.Warning) {
        console.warn(redactAccessToken(message));
      }
    },
  };

  class StatusRecordingHttpClient extends signalr.DefaultHttpClient {
    lastStatus: number | undefined;

    override async send(request: HttpRequest): Promise<HttpResponse> {
      try {
        const response = await super.send(request);
        this.lastStatus = response.statusCode;
        return response;
      } catch (error) {
        this.lastStatus = error instanceof signalr.HttpError ? error.statusCode : undefined;
        throw error;
      }
    }
  }

  const httpClient = new StatusRecordingHttpClient(logger);
  const connection = new signalr.HubConnectionBuilder()
    .withUrl(hubUrl(), { accessTokenFactory: () => accessToken() ?? '', httpClient })
    .configureLogging(logger)
    .build();

  let stopped = false;
  connection.on('hints', (batch: HintBatch) => onHints(batch));
  connection.onclose(() => {
    if (!stopped) {
      onClose();
    }
  });

  return {
    async start() {
      try {
        await connection.start();
      } catch (error) {
        throw httpClient.lastStatus === 401 ? new HubUnauthorizedError() : error;
      }
    },
    async stop() {
      stopped = true;
      await connection.stop();
    },
  };
};
