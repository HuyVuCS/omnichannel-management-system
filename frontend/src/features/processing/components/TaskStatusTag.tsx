import { Tag } from 'antd';
import { useTranslation } from 'react-i18next';
import type { TaskStatus } from '../processingApi';

/** One status colour per task status, the same on every page. */
const STATUS_COLORS: Record<TaskStatus, string> = {
  Pending: 'default',
  InProgress: 'processing',
  RetryScheduled: 'warning',
  Succeeded: 'success',
  Failed: 'error',
};

/** The status of a processing task as the API reports it; a value this client does not know yet is shown as it is. */
export function TaskStatusTag({ status }: { status: TaskStatus }) {
  const { t } = useTranslation();

  return (
    <Tag color={STATUS_COLORS[status] ?? 'default'} style={{ marginInlineEnd: 0 }}>
      {t(`processing.taskStatuses.${status}`, status)}
    </Tag>
  );
}
