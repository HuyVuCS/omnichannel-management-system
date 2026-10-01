import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { afterAll, afterEach, beforeAll, vi } from 'vitest';
import i18n, { DEFAULT_LANGUAGE } from '@/shared/i18n';
import { server } from './server';

// Pages are separate chunks (React.lazy): the first render of a page in a test file also loads and transforms its
// modules, which can take longer than the default 1 s when several test files run in parallel.
configure({ asyncUtilTimeout: 5000 });

// jsdom has no layout engine: the browser APIs AntD probes for are stubbed.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string): MediaQueryList => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(() => false),
  }),
});

class ResizeObserverStub implements ResizeObserver {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
}

window.ResizeObserver = ResizeObserverStub;

// jsdom does not implement pseudo-element styles and logs "not implemented" when AntD asks for them.
const originalGetComputedStyle = window.getComputedStyle.bind(window);
window.getComputedStyle = (element: Element) => originalGetComputedStyle(element);

// Every request a test makes must be answered by a handler of that test (or a default one).
beforeAll(() => server.listen({ onUnhandledFrame: 'error' }));

afterEach(async () => {
  cleanup();
  server.resetHandlers();
  await i18n.changeLanguage(DEFAULT_LANGUAGE);
  window.sessionStorage.clear();
  window.localStorage.clear();
});

afterAll(() => server.close());
