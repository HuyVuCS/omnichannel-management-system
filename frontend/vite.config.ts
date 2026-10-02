import { fileURLToPath, URL } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The API runs on http://localhost:5080 (backend launchSettings). In development the dev server proxies /api to it,
// so the browser only ever talks to its own origin (no CORS, same paths as in production).
export default defineConfig({
  plugins: [react()],
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
    // for memory while the API, PostgreSQL and RabbitMQ run next to them, and first renders time out. Four workers take
    // about as long in total and are stable.
    maxWorkers: 4,
  },
});
