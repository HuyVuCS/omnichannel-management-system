import { Typography } from 'antd';
import type { ReactNode } from 'react';
import { useHeadingLook } from '@/shared/hooks/useHeadingLook';

interface PageHeaderProps {
  title: string;
  /** Page-level actions (e.g. the "new" button), right-aligned. */
  extra?: ReactNode;
}

const HEIGHT = 48;

/**
 * Title row of a page. Fixed height and a truncated title, so its size does not depend on the language. The title is the
 * page's one `<h1>`, with the look of a level-4 heading.
 */
export function PageHeader({ title, extra }: PageHeaderProps) {
  const look = useHeadingLook(4);
  return (
    <div style={{ height: HEIGHT, display: 'flex', alignItems: 'center', gap: 16, marginBottom: 16 }}>
      <Typography.Title level={1} ellipsis={{ tooltip: title }} style={{ ...look, margin: 0, flex: 1, minWidth: 0 }}>
        {title}
      </Typography.Title>
      {extra && <div style={{ flex: 'none' }}>{extra}</div>}
    </div>
  );
}
