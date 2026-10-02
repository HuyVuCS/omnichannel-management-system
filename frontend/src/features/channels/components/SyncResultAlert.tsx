import { QuestionCircleOutlined } from '@ant-design/icons';
import { Alert, Space, Spin, theme, Tooltip } from 'antd';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { TaskStatusTag } from '@/features/processing/components/TaskStatusTag';
import { processingTaskPath } from '@/features/processing/paths';
import { useGetProcessingTaskQuery } from '@/features/processing/processingApi';
import type { SavedChannelProduct } from '../channelsApi';

interface SyncResultAlertProps {
  /** The API's answer to the last saved mapping. */
  saved: SavedChannelProduct;
  onDismiss: () => void;
}

/** The task a saved mapping started: its status as the API reports it, and the way to its page. */
function StartedSync({ taskId }: { taskId: string }) {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  // Fetched when the alert appears; a realtime hint that names the task fetches it again (`features/realtime`).
  // `currentData`, not `data`: while the task of the next save is loading, the status of the previous save's task must
  // not be shown as its status.
  const { currentData: task } = useGetProcessingTaskQuery(taskId, { refetchOnMountOrArgChange: true });

  return (
    <Space size={12} data-testid="sync-result-task">
      {t('channelDetail.sync.taskStatus')}
      {task ? <TaskStatusTag status={task.status} /> : <Spin size="small" />}
      <Link to={processingTaskPath(taskId)} style={{ color: token.colorLink }}>
        {t('channelDetail.sync.openTask')}
      </Link>
    </Space>
  );
}

/**
 * What the API answered about stock sync when a mapping was saved, shown above the mapping list until it is dismissed
 * or the next save replaces it:
 * - with a `syncTaskId`: a stock sync was started - the task's status (live while the realtime connection is) and a
 *   link to the task;
 * - without: none was started. Which of the API's documented reasons applied is not guessed here; all of them are
 *   listed on hover.
 */
export function SyncResultAlert({ saved, onDismiss }: SyncResultAlertProps) {
  const { t } = useTranslation();
  const closable = { 'aria-label': t('common.close'), onClose: onDismiss };

  if (saved.syncTaskId) {
    return (
      <Alert
        type="info"
        showIcon
        closable={closable}
        title={t('channelDetail.sync.started', { sku: saved.sku })}
        description={<StartedSync taskId={saved.syncTaskId} />}
        style={{ marginBottom: 16 }}
        data-testid="sync-result"
      />
    );
  }

  return (
    <Alert
      type="info"
      showIcon
      closable={closable}
      title={
        <Space size={8}>
          {t('channelDetail.sync.none', { sku: saved.sku })}
          <Tooltip title={t('channelDetail.sync.noneReasons')}>
            <QuestionCircleOutlined tabIndex={0} aria-label={t('channelDetail.sync.noneWhy')} />
          </Tooltip>
        </Space>
      }
      style={{ marginBottom: 16 }}
      data-testid="sync-result"
    />
  );
}
