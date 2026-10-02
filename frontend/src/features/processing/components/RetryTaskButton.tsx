import { RedoOutlined } from '@ant-design/icons';
import { App as AntApp, Button } from 'antd';
import { useTranslation } from 'react-i18next';
import { useHasAnyRole } from '@/features/auth/useHasAnyRole';
import { problemMessages } from '@/shared/api/problemDetails';
import { SECONDARY_ACTION_WIDTH } from '@/shared/components/layout';
import { RETRY_ROLES } from '../paths';
import { useRetryProcessingTaskMutation, type TaskStatus } from '../processingApi';

interface RetryTaskButtonProps {
  taskId: string;
  status: TaskStatus;
  /** The order of the task, when it has one: the order's page lists its tasks and is fetched again. */
  orderId?: string | null;
}

/**
 * "Retry" for a failed task: shown to Manager and Admin only, and only while the task is `Failed` (the API enforces
 * both). After a confirmation the task is queued again; it then shows `Pending` because everything on screen about it
 * is fetched again (see `retryProcessingTask`). What the API refuses - the task is not failed any more, it does not
 * exist - is said in the UI language; network errors and 5xx are reported globally.
 */
export function RetryTaskButton({ taskId, status, orderId }: RetryTaskButtonProps) {
  const { t } = useTranslation();
  const { modal, message } = AntApp.useApp();
  const canRetry = useHasAnyRole(RETRY_ROLES);
  const [retry, { isLoading }] = useRetryProcessingTaskMutation();

  if (!canRetry || status !== 'Failed') {
    return null;
  }

  const confirmRetry = () => {
    modal.confirm({
      title: t('processing.retry.confirmTitle'),
      content: t('processing.retry.confirmContent'),
      okText: t('processing.retry.action'),
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          await retry({ taskId, orderId }).unwrap();
          message.success(t('processing.retry.requested'));
        } catch (error) {
          const messages = problemMessages(t, error);
          if (messages.length > 0) {
            message.error(messages.join(' '));
          }
        }
      },
    });
  };

  return (
    <Button
      icon={<RedoOutlined />}
      loading={isLoading}
      onClick={confirmRetry}
      style={{ width: SECONDARY_ACTION_WIDTH }}
    >
      {t('processing.retry.action')}
    </Button>
  );
}
