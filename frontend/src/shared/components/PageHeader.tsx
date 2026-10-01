import { Typography } from 'antd';
import type { ReactNode } from 'react';

interface PageHeaderProps {
  title: string;
  /** Page-level actions (e.g. the "new" button), right-aligned. */
  extra?: ReactNode;
}

const HEIGHT = 48;

/** Title row of a page. Fixed height and a truncated title, so its size does not depend on the language. */
export function PageHeader({ title, extra }: PageHeaderProps) {
  return (
    <div style={{ height: HEIGHT, display: 'flex', alignItems: 'center', gap: 16, marginBottom: 16 }}>
      <Typography.Title level={4} ellipsis={{ tooltip: title }} style={{ margin: 0, flex: 1, minWidth: 0 }}>
        {title}
      </Typography.Title>
      {extra && <div style={{ flex: 'none' }}>{extra}</div>}
    </div>
  );
}
