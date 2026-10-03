import { FallOutlined, StopOutlined, SyncOutlined, WarningOutlined } from '@ant-design/icons';
import { Tag } from 'antd';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { AlertType } from '../alertsApi';

/** One icon per alert type, the same on every page; the text next to it says what it is. */
const TYPE_ICONS: Record<AlertType, ReactNode> = {
  LowStock: <FallOutlined />,
  InsufficientStock: <StopOutlined />,
  StockSyncFailed: <SyncOutlined />,
  ProcessingFailed: <WarningOutlined />,
};

/** The type of an alert as the API reports it; a value this client does not know yet is shown as it is. */
export function AlertTypeTag({ type }: { type: AlertType }) {
  const { t } = useTranslation();

  return (
    <Tag icon={TYPE_ICONS[type]} style={{ marginInlineEnd: 0 }}>
      {t(`alerts.types.${type}`, type)}
    </Tag>
  );
}
