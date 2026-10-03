import { CheckCircleOutlined, ExclamationCircleOutlined, EyeOutlined } from '@ant-design/icons';
import { Tag } from 'antd';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { AlertStatus } from '../alertsApi';

/** One colour and one icon per alert status; the text says it too, so the status never depends on colour alone. */
const STATUS_LOOK: Record<AlertStatus, { color: string; icon: ReactNode }> = {
  Open: { color: 'error', icon: <ExclamationCircleOutlined /> },
  Acknowledged: { color: 'warning', icon: <EyeOutlined /> },
  Resolved: { color: 'success', icon: <CheckCircleOutlined /> },
};

/** The status of an alert as the API reports it; a value this client does not know yet is shown as it is. */
export function AlertStatusTag({ status }: { status: AlertStatus }) {
  const { t } = useTranslation();
  const look = STATUS_LOOK[status];

  return (
    <Tag color={look?.color ?? 'default'} icon={look?.icon} style={{ marginInlineEnd: 0 }}>
      {t(`alerts.statuses.${status}`, status)}
    </Tag>
  );
}
