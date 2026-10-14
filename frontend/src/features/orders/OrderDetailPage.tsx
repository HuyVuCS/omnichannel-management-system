import { ArrowLeftOutlined, ReloadOutlined, SwapOutlined } from '@ant-design/icons';
import { skipToken } from '@reduxjs/toolkit/query';
import {
  Alert,
  Button,
  Card,
  Descriptions,
  Empty,
  Flex,
  Result,
  Space,
  Table,
  Typography,
  type TableProps,
} from 'antd';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate, useParams } from 'react-router';
import { movementsPath } from '@/features/inventory/paths';
import { AttemptsTable } from '@/features/processing/components/AttemptsTable';
import { RetryTaskButton } from '@/features/processing/components/RetryTaskButton';
import { TaskStatusTag } from '@/features/processing/components/TaskStatusTag';
import { problemMessages } from '@/shared/api/problemDetails';
import { isApiError } from '@/shared/api/types';
import {
  PAGE_ACTION_WIDTH,
  SECONDARY_ACTION_WIDTH,
  SHORT_SUMMARY_LABEL_WIDTH,
  SUMMARY_LABEL_WIDTH,
  WIDE_ACTION_WIDTH,
} from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { ResultTitle } from '@/shared/components/ResultTitle';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { formatDateTime, formatMoney, formatNumber } from '@/shared/utils/format';
import { isGuid } from '@/shared/utils/guid';
import { OrderPaymentCard } from './components/OrderPaymentCard';
import { OrderStatusTag } from './components/OrderStatusTag';
import { useGetOrderQuery, type OrderItem, type OrderTask } from './ordersApi';
import { orderListAddress, ORDERS_PATH } from './paths';

/** A value the user may need elsewhere (a log search, a bug report): shown in full with a copy button. */
function Copyable({ text }: { text: string }) {
  return (
    <Typography.Text code copyable>
      {text}
    </Typography.Text>
  );
}

/**
 * One order (every role): its summary, its payment with the invoice and the confirmation e-mail that follow from it,
 * its lines and every processing task with all attempts - which worker instance ran it, when, and what went wrong.
 * Everything is shown as the API sends it; no amount is computed here. The order is fetched again whenever the page
 * is opened, on "Refresh" and when a realtime hint names it or one of its tasks (they change in the background, and a
 * payment can be recorded in another session). Manager and Admin can record the payment and retry a failed task here.
 *
 * An id in the address that is no GUID is not sent; it and an order the API does not know end on the not-found view.
 */
export function OrderDetailPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const { orderId } = useParams();
  const validId = isGuid(orderId) ? orderId : undefined;
  const {
    data: order,
    error,
    isError,
    isFetching,
    refetch,
  } = useGetOrderQuery(validId ?? skipToken, { refetchOnMountOrArgChange: true });

  // The list as the user left it (page, sort, filters) when the detail was opened from there.
  const listAddress = orderListAddress(location.state) ?? ORDERS_PATH;
  const notFound = validId === undefined || (isError && isApiError(error) && error.status === 404);

  const lineColumns = useMemo<TableProps<OrderItem>['columns']>(
    () => [
      {
        key: 'sku',
        dataIndex: 'sku',
        title: t('orderDetail.lines.sku'),
        width: 180,
        ellipsis: { showTitle: false },
        render: (sku: string) => <TruncatedText text={sku} />,
      },
      {
        key: 'productName',
        dataIndex: 'productName',
        title: t('orderDetail.lines.productName'),
        width: 300,
        ellipsis: { showTitle: false },
        render: (name: string) => <TruncatedText text={name} />,
      },
      {
        key: 'stock',
        dataIndex: 'stock',
        title: t('orderDetail.lines.quantity'),
        width: 120,
        align: 'right',
        ellipsis: true,
        render: (stock: number) => formatNumber(stock),
      },
      {
        key: 'unitPrice',
        dataIndex: 'unitPrice',
        title: t('orderDetail.lines.unitPrice'),
        width: 170,
        align: 'right',
        ellipsis: true,
        render: (unitPrice: number) => formatMoney(unitPrice),
      },
      {
        key: 'lineTotal',
        dataIndex: 'lineTotal',
        title: t('orderDetail.lines.lineTotal'),
        width: 170,
        align: 'right',
        ellipsis: true,
        render: (lineTotal: number) => formatMoney(lineTotal),
      },
    ],
    [t],
  );

  if (notFound) {
    return (
      <Result
        status="404"
        title={<ResultTitle>{t('orderDetail.notFound.title')}</ResultTitle>}
        subTitle={t('orderDetail.notFound.description')}
        extra={
          <Button type="primary" onClick={() => void navigate(ORDERS_PATH)} style={{ width: PAGE_ACTION_WIDTH }}>
            {t('orderDetail.notFound.toList')}
          </Button>
        }
      />
    );
  }

  // What the user can act on (e.g. no permission); network errors and 5xx are reported globally.
  const errorMessages = isError ? problemMessages(t, error) : [];

  const taskItems = (task: OrderTask) => [
    {
      key: 'taskType',
      label: t('orderDetail.tasks.type'),
      children: t(`processing.taskTypes.${task.taskType}`, task.taskType),
    },
    { key: 'status', label: t('orderDetail.tasks.status'), children: <TaskStatusTag status={task.status} /> },
    { key: 'retryCount', label: t('orderDetail.tasks.retryCount'), children: formatNumber(task.retryCount) },
    {
      key: 'correlationId',
      label: t('orderDetail.tasks.correlationId'),
      children: <Copyable text={task.correlationId} />,
    },
    { key: 'messageId', label: t('orderDetail.tasks.messageId'), children: task.messageId ?? '' },
    { key: 'createdAt', label: t('orderDetail.tasks.createdAt'), children: formatDateTime(task.createdAt) },
    { key: 'updatedAt', label: t('orderDetail.tasks.updatedAt'), children: formatDateTime(task.updatedAt) },
  ];

  return (
    <>
      <PageHeader
        title={
          order ? t('orderDetail.title', { externalOrderId: order.externalOrderId }) : t('orderDetail.loadingTitle')
        }
        extra={
          <Space>
            <Button
              icon={<ArrowLeftOutlined />}
              onClick={() => void navigate(listAddress)}
              style={{ width: SECONDARY_ACTION_WIDTH }}
            >
              {t('orderDetail.back')}
            </Button>
            <Button
              icon={<ReloadOutlined />}
              loading={isFetching}
              onClick={() => void refetch()}
              style={{ width: SECONDARY_ACTION_WIDTH }}
            >
              {t('common.refresh')}
            </Button>
            <Button
              icon={<SwapOutlined />}
              onClick={() => void navigate(movementsPath({ referenceType: 'Order', referenceId: validId }))}
              style={{ width: WIDE_ACTION_WIDTH }}
            >
              {t('orderDetail.movements')}
            </Button>
          </Space>
        }
      />
      {errorMessages.length > 0 && (
        <Alert type="error" showIcon title={errorMessages.join(' ')} style={{ marginBottom: 16 }} />
      )}
      {order && (
        <Flex vertical gap={16}>
          <Descriptions
            bordered
            size="small"
            column={2}
            styles={{ label: { width: SUMMARY_LABEL_WIDTH } }}
            items={[
              {
                key: 'externalOrderId',
                label: t('orderDetail.summary.externalOrderId'),
                children: order.externalOrderId,
              },
              {
                key: 'status',
                label: t('orderDetail.summary.status'),
                children: <OrderStatusTag status={order.status} />,
              },
              {
                key: 'channel',
                label: t('orderDetail.summary.channel'),
                children: `${order.channel.name} (${order.channel.slug})`,
              },
              { key: 'customer', label: t('orderDetail.summary.customer'), children: order.customer.name },
              {
                key: 'itemCount',
                label: t('orderDetail.summary.itemCount'),
                children: formatNumber(order.itemCount),
              },
              {
                key: 'totalAmount',
                label: t('orderDetail.summary.totalAmount'),
                children: formatMoney(order.totalAmount),
              },
              {
                key: 'createdAt',
                label: t('orderDetail.summary.createdAt'),
                children: formatDateTime(order.createdAt),
              },
              { key: 'orderId', label: t('orderDetail.summary.orderId'), children: <Copyable text={order.orderId} /> },
            ]}
          />

          <OrderPaymentCard order={order} />

          <Card title={t('orderDetail.lines.title')} size="small">
            <Table<OrderItem>
              rowKey="productId"
              size="small"
              columns={lineColumns}
              dataSource={order.items}
              tableLayout="fixed"
              // Fits into the card at the smallest supported window (1280 px): no scrollbar of its own there.
              scroll={{ x: 940 }}
              pagination={false}
            />
          </Card>

          <Card title={t('orderDetail.tasks.title')} size="small">
            {order.tasks.length === 0 ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('orderDetail.tasks.none')} />
            ) : (
              <Flex vertical gap={24}>
                {order.tasks.map((task) => (
                  <div key={task.taskId} data-testid={`order-task-${task.taskId}`}>
                    <Descriptions
                      bordered
                      size="small"
                      column={2}
                      styles={{ label: { width: SHORT_SUMMARY_LABEL_WIDTH } }}
                      style={{ marginBottom: 12 }}
                      items={taskItems(task)}
                    />
                    {task.attempts.length === 0 ? (
                      <Typography.Text type="secondary">{t('orderDetail.tasks.noAttempts')}</Typography.Text>
                    ) : (
                      <AttemptsTable
                        attempts={task.attempts}
                        // One table per task on this page: each scroll region gets a name of its own.
                        label={`${t('processing.detail.attempts.title')}: ${t(`processing.taskTypes.${task.taskType}`, task.taskType)}`}
                      />
                    )}
                    <div style={{ marginTop: 12 }}>
                      <RetryTaskButton taskId={task.taskId} status={task.status} orderId={order.orderId} />
                    </div>
                  </div>
                ))}
              </Flex>
            )}
          </Card>
        </Flex>
      )}
    </>
  );
}
