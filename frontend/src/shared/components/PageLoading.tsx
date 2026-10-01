import { Spin } from 'antd';
import { HEADER_HEIGHT } from './layout';

/** Padding of the layout's content area (top + bottom), see AppLayout. */
const CONTENT_PADDING = 48;

/**
 * Suspense fallback while the code of a page loads. It fills the content area (or the whole window for the login page),
 * so the shell does not jump when the page arrives.
 */
export function PageLoading({ fullScreen = false }: { fullScreen?: boolean }) {
  return (
    <div
      data-testid="page-loading"
      style={{
        minHeight: fullScreen ? '100vh' : `calc(100vh - ${HEADER_HEIGHT + CONTENT_PADDING}px)`,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
      }}
    >
      <Spin size="large" />
    </div>
  );
}
