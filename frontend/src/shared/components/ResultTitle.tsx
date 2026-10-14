import type { CSSProperties, ReactNode } from 'react';

const STYLE: CSSProperties = { font: 'inherit', color: 'inherit', margin: 0 };

/**
 * The title of a page that is only a `Result` (not found, forbidden, route error): the page's one `<h1>` (task 027 A4),
 * with the look of the `Result` title around it.
 */
export function ResultTitle({ children }: { children: ReactNode }) {
  return <h1 style={STYLE}>{children}</h1>;
}
