import { CloseCircleOutlined, ExclamationCircleOutlined, InfoCircleOutlined, WarningOutlined } from '@ant-design/icons';
import { Tag } from 'antd';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { EventLevel } from '../systemEventsApi';

/** One colour and one icon per level; the text says it too, so the level never depends on colour alone. */
const LEVEL_LOOK: Record<EventLevel, { color: string; icon: ReactNode }> = {
  Information: { color: 'default', icon: <InfoCircleOutlined /> },
  Warning: { color: 'warning', icon: <WarningOutlined /> },
  Error: { color: 'error', icon: <CloseCircleOutlined /> },
  Critical: { color: 'error', icon: <ExclamationCircleOutlined /> },
};

/** The level of an event as the API reports it; a value this client does not know yet is shown as it is. */
export function EventLevelTag({ level }: { level: EventLevel }) {
  const { t } = useTranslation();
  const look = LEVEL_LOOK[level];

  return (
    <Tag color={look?.color ?? 'default'} icon={look?.icon} style={{ marginInlineEnd: 0 }}>
      {t(`logs.levels.${level}`, level)}
    </Tag>
  );
}
