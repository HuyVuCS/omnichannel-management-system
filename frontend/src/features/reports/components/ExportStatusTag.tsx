import { Tag } from 'antd';
import { useTranslation } from 'react-i18next';
import { TaskStatusTag } from '@/features/processing/components/TaskStatusTag';
import type { ExportStatus } from '../reportsApi';

/**
 * The status of an export: the status of its task, with the tag every page uses for it, or "Expired" (grey - nothing
 * is wrong, the retention time is over).
 */
export function ExportStatusTag({ status }: { status: ExportStatus }) {
  const { t } = useTranslation();

  return status === 'Expired' ? (
    <Tag style={{ marginInlineEnd: 0 }}>{t('reports.statuses.Expired')}</Tag>
  ) : (
    <TaskStatusTag status={status} />
  );
}
