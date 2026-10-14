import { ArrowLeftOutlined, ReloadOutlined } from '@ant-design/icons';
import { skipToken } from '@reduxjs/toolkit/query';
import { Alert, Button, Card, Descriptions, Flex, Result, Space, Tag, theme, Typography } from 'antd';
import { useTranslation } from 'react-i18next';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import { useListChannelsQuery } from '@/features/channels/channelsApi';
import { orderPath } from '@/features/orders/paths';
import { problemMessages } from '@/shared/api/problemDetails';
import { isApiError } from '@/shared/api/types';
import { PAGE_ACTION_WIDTH, SECONDARY_ACTION_WIDTH, SHORT_SUMMARY_LABEL_WIDTH } from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { ResultTitle } from '@/shared/components/ResultTitle';
import { formatDateTime, formatNumber } from '@/shared/utils/format';
import { isGuid } from '@/shared/utils/guid';
import { AttemptsTable } from './components/AttemptsTable';
import { RetryTaskButton } from './components/RetryTaskButton';
import { TaskStatusTag } from './components/TaskStatusTag';
import { PROCESSING_PATH, processingListAddress, processingListPath } from './paths';
import { useGetProcessingTaskQuery, type MessageStatus } from './processingApi';

/** One colour per publish state of a message. */
const MESSAGE_COLORS: Record<MessageStatus, string> = {
  Pending: 'default',
  Published: 'success',
  Failed: 'error',
};

/** A value the user may need elsewhere (a log search, a bug report): shown in full with a copy button. */
function Copyable({ text }: { text: string }) {
  return (
    <Typography.Text code copyable>
      {text}
    </Typography.Text>
  );
}

/**
 * One processing task (every role): what it is for (type, channel, product, order), where it stands, the publish state
 * of its message and every attempt - which worker instance ran it, when, and what went wrong. Everything is shown as
 * the API sends it. The message's `lastError` is never shown (it is not sanitised for the UI); the attempts' error
 * messages are.
 *
 * The task is fetched again whenever the page is opened, on "Refresh", and when a realtime hint names it. Manager and
 * Admin can retry a failed task from here.
 *
 * An id in the address that is no GUID is not sent; it and a task the API does not know end on the not-found view.
 */
export function ProcessingTaskPage() {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  const navigate = useNavigate();
  const location = useLocation();
  const { taskId } = useParams();
  const validId = isGuid(taskId) ? taskId : undefined;
  const {
    data: task,
    error,
    isError,
    isFetching,
    refetch,
  } = useGetProcessingTaskQuery(validId ?? skipToken, { refetchOnMountOrArgChange: true });
  const channels = useListChannelsQuery();

  // The list as the user left it (page, sort, filters) when the detail was opened from there.
  const listAddress = processingListAddress(location.state) ?? PROCESSING_PATH;
  const notFound = validId === undefined || (isError && isApiError(error) && error.status === 404);

  if (notFound) {
    return (
      <Result
        status="404"
        title={<ResultTitle>{t('processing.detail.notFound.title')}</ResultTitle>}
        subTitle={t('processing.detail.notFound.description')}
        extra={
          <Button type="primary" onClick={() => void navigate(PROCESSING_PATH)} style={{ width: PAGE_ACTION_WIDTH }}>
            {t('processing.detail.notFound.toList')}
          </Button>
        }
      />
    );
  }

  // What the user can act on (e.g. no permission); network errors and 5xx are reported globally.
  const errorMessages = isError ? problemMessages(t, error) : [];
  // The channel's name comes from the channel list; a channel it does not contain is shown by its id.
  const channelName = task?.channelId
    ? (channels.data?.find((channel) => channel.channelId === task.channelId)?.name ?? task.channelId)
    : '';

  return (
    <>
      <PageHeader
        title={task ? t(`processing.taskTypes.${task.taskType}`, task.taskType) : t('processing.detail.loadingTitle')}
        extra={
          <Space>
            <Button
              icon={<ArrowLeftOutlined />}
              onClick={() => void navigate(listAddress)}
              style={{ width: SECONDARY_ACTION_WIDTH }}
            >
              {t('processing.detail.back')}
            </Button>
            <Button
              icon={<ReloadOutlined />}
              loading={isFetching}
              onClick={() => void refetch()}
              style={{ width: SECONDARY_ACTION_WIDTH }}
            >
              {t('common.refresh')}
            </Button>
            {task && <RetryTaskButton taskId={task.taskId} status={task.status} orderId={task.orderId} />}
          </Space>
        }
      />
      {errorMessages.length > 0 && (
        <Alert type="error" showIcon title={errorMessages.join(' ')} style={{ marginBottom: 16 }} />
      )}
      {task && (
        <Flex vertical gap={16}>
          <Descriptions
            bordered
            size="small"
            column={2}
            styles={{ label: { width: SHORT_SUMMARY_LABEL_WIDTH } }}
            data-testid="task-summary"
            items={[
              {
                key: 'taskType',
                label: t('processing.detail.taskType'),
                children: t(`processing.taskTypes.${task.taskType}`, task.taskType),
              },
              { key: 'status', label: t('processing.detail.status'), children: <TaskStatusTag status={task.status} /> },
              { key: 'channel', label: t('processing.detail.channel'), children: channelName },
              {
                key: 'productSku',
                label: t('processing.detail.productSku'),
                // Every task of the product: its pushes to the other channels, the ones before and after this one.
                children: task.productId ? (
                  <Space size={12}>
                    {task.productSku ?? ''}
                    <Link to={processingListPath({ productId: task.productId })} style={{ color: token.colorLink }}>
                      {t('processing.detail.productTasks')}
                    </Link>
                  </Space>
                ) : (
                  ''
                ),
              },
              {
                key: 'order',
                label: t('processing.detail.order'),
                children: task.orderId && (
                  <Link to={orderPath(task.orderId)} style={{ color: token.colorLink }}>
                    {t('processing.openOrder')}
                  </Link>
                ),
              },
              {
                key: 'retryCount',
                label: t('processing.detail.retryCount'),
                children: formatNumber(task.retryCount),
              },
              {
                key: 'correlationId',
                label: t('processing.detail.correlationId'),
                children: <Copyable text={task.correlationId} />,
              },
              { key: 'taskId', label: t('processing.detail.taskId'), children: <Copyable text={task.taskId} /> },
              { key: 'createdAt', label: t('processing.detail.createdAt'), children: formatDateTime(task.createdAt) },
              { key: 'updatedAt', label: t('processing.detail.updatedAt'), children: formatDateTime(task.updatedAt) },
            ]}
          />

          <Card title={t('processing.detail.message.title')} size="small" data-testid="task-message">
            {task.message ? (
              <Descriptions
                bordered
                size="small"
                column={2}
                styles={{ label: { width: SHORT_SUMMARY_LABEL_WIDTH } }}
                items={[
                  {
                    key: 'eventType',
                    label: t('processing.detail.message.eventType'),
                    children: task.message.eventType,
                  },
                  {
                    key: 'status',
                    label: t('processing.detail.message.status'),
                    children: (
                      <Tag color={MESSAGE_COLORS[task.message.status] ?? 'default'}>
                        {t(`processing.messageStatuses.${task.message.status}`, task.message.status)}
                      </Tag>
                    ),
                  },
                  {
                    key: 'createdAt',
                    label: t('processing.detail.message.createdAt'),
                    children: formatDateTime(task.message.createdAt),
                  },
                  {
                    key: 'publishedAt',
                    label: t('processing.detail.message.publishedAt'),
                    children: formatDateTime(task.message.publishedAt),
                  },
                  {
                    key: 'publishAttempts',
                    label: t('processing.detail.message.publishAttempts'),
                    children: formatNumber(task.message.publishAttempts),
                  },
                  {
                    key: 'messageId',
                    label: t('processing.detail.message.messageId'),
                    children: task.message.outboxEventId,
                  },
                ]}
              />
            ) : (
              <Typography.Text type="secondary">{t('processing.detail.message.none')}</Typography.Text>
            )}
          </Card>

          <Card title={t('processing.detail.attempts.title')} size="small" data-testid="task-attempts">
            {task.attempts.length === 0 ? (
              <Typography.Text type="secondary">{t('processing.detail.attempts.none')}</Typography.Text>
            ) : (
              <AttemptsTable attempts={task.attempts} />
            )}
          </Card>
        </Flex>
      )}
    </>
  );
}
