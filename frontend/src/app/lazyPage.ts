import { lazy, type ComponentType } from 'react';

/**
 * The code of a page could not be loaded. The usual cause is a new deployment while the tab was open (the chunk file
 * of the old build no longer exists); a network failure looks the same. Only a reload can fix it.
 */
export class PageLoadError extends Error {
  constructor(cause: unknown) {
    super('The code of this page could not be loaded.', { cause });
    this.name = 'PageLoadError';
  }
}

/**
 * `React.lazy` for a route's page. A failed import is rethrown as {@link PageLoadError}, so the route error page can
 * tell "reload to get the new version" from a page that broke while rendering - without guessing from the browser's
 * error text, which differs per browser.
 */
export function lazyPage(load: () => Promise<{ default: ComponentType }>) {
  return lazy(() =>
    load().catch((cause: unknown) => {
      throw new PageLoadError(cause);
    }),
  );
}
