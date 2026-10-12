import { fileURLToPath, URL } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';
import { contentSecurityPolicy } from './vite/contentSecurityPolicy.ts';

// The API runs on http://localhost:5080 (backend launchSettings). In development the dev server proxies /api to it,
// so the browser only ever talks to its own origin (no CORS, same paths as in production).
export default defineConfig({
  // The built index.html gets its Content Security Policy from VITE_API_BASE_URL / VITE_STORAGE_ORIGIN (build only).
  plugins: [react(), contentSecurityPolicy()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      // `ws`: the notifications hub (`/api/hubs/notifications`) uses WebSockets.
      '/api': { target: 'http://localhost:5080', changeOrigin: false, ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    testTimeout: 20_000,
    restoreMocks: true,
    // Every test file renders the whole application in its own jsdom. With one worker per CPU core the files compete
    // for memory while PostgreSQL and RabbitMQ run next to them, and first renders time out.
    // 2026-10-03 (task 017): measured with PostgreSQL and RabbitMQ running, three full runs each, all green -
    // 4 workers 207 / 183 / 178 s (median 183 s), 3 workers 217 / 207 / 195 s (median 207 s, 13 % slower).
    // 3 would have been chosen at no more than 5 % above 4, so 4 stays. Stability came from splitting the tests that
    // took more than 5 s alone and from running the suite without an API or Workers host next to it (smoke scripts).
    maxWorkers: 4,
  },
});
