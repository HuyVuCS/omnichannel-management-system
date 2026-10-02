import { skipToken } from '@reduxjs/toolkit/query';
import { Alert, Button, Descriptions, Flex, Space, Spin, theme, Typography, type AlertProps } from 'antd';
import type { TFunction } from 'i18next';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { TaskStatusTag } from '@/features/processing/components/TaskStatusTag';
import { isTerminalTaskStatus, processingApi, type ProcessingTaskDetail } from '@/features/processing/processingApi';
import { selectIsLive } from '@/features/realtime/realtimeSlice';
import { baseApi } from '@/shared/api/baseApi';
import { problemMessages } from '@/shared/api/problemDetails';
import { PAGE_ACTION_WIDTH, SECONDARY_ACTION_WIDTH, SUMMARY_LABEL_WIDTH } from '@/shared/components/layout';
import { useAppDispatch, useAppSelector } from '@/shared/hooks/store';
import { formatMoney } from '@/shared/utils/format';
import { ORDER_LIST_TAG, useGetOrderQuery, type OrderDetail } from '../ordersApi';
import { orderPath } from '../paths';
import type { AcceptedOrder } from './OrderEntryForm';
import { OrderStatusTag } from './OrderStatusTag';

/** How often the task is asked for while it is not finished. */
export const POLL_INTERVAL_MS = 1500;

/** For how long after the order was accepted (or after "Check again") the task is waited for. */
export const POLL_WINDOW_MS = 60_000;

interface OrderOutcomeProps {
  /** Channel slug of the panel (identifies this view in the page). */
  slug: string;
  /** What the panel shows as its channel. */
  channelLabel: string;
  accepted: AcceptedOrder;
  /** Back to an empty form with a new external order id. */
  onNewOrder: () => void;
}

interface StateMessage {
  type: NonNullable<AlertProps['type']>;
  title: string;
  description?: ReactNode;
}

/**
 * What the task and the order say right now, as one message. Every fact comes from the API: the task's status, the
 * publish state of its message, the last attempt's error, the order's status. The message's `lastError` is never
 * shown (it is not sanitised for the UI); the attempt's `errorMessage` is.
 */
function describeState(
  t: TFunction,
  task: ProcessingTaskDetail,
  order: OrderDetail | undefined,
  orderLoading: boolean,
): StateMessage | undefined {
  const lastAttempt = task.attempts.at(-1);

  if (task.status === 'Failed') {
    return {
      type: 'error',
      title: t('orderEntry.outcome.failed'),
      description: (
        <>
          {lastAttempt?.errorCode && (
            <div>
              {t('orderEntry.outcome.errorCode')} <Typography.Text code>{lastAttempt.errorCode}</Typography.Text>
            </div>
          )}
          {lastAttempt?.errorMessage && <div>{lastAttempt.errorMessage}</div>}
          <div>
            {t('errors.correlationId')}{' '}
            <Typography.Text code copyable>
              {task.correlationId}
            </Typography.Text>
          </div>
        </>
      ),
    };
  }

  if (task.status === 'Succeeded') {
    if (order?.status === 'Reserved') {
      return { type: 'success', title: t('orderEntry.outcome.reserved') };
    }

    if (order?.status === 'InsufficientStock') {
      return { type: 'warning', title: t('orderEntry.outcome.insufficientStock') };
    }

    // The order is on its way: what it says decides the message. (Found in the browser: a green "processed" showed
    // for a moment before "not enough stock".)
    return orderLoading ? undefined : { type: 'success', title: t('orderEntry.outcome.processed') };
  }

  if (task.message?.status === 'Failed') {
    return { type: 'error', title: t('orderEntry.outcome.notSent') };
  }

  if (task.status === 'RetryScheduled') {
    return {
      type: 'warning',
      title: t('orderEntry.outcome.retrying'),
      description: lastAttempt?.errorCode ? (
        <>
          {t('orderEntry.outcome.errorCode')} <Typography.Text code>{lastAttempt.errorCode}</Typography.Text>
        </>
      ) : undefined,
    };
  }

  if (task.status === 'InProgress') {
    return { type: 'info', title: t('orderEntry.outcome.inProgress') };
  }

  return task.message?.status === 'Pending'
    ? { type: 'info', title: t('orderEntry.outcome.waitingForQueue') }
    : { type: 'info', title: t('orderEntry.outcome.queued') };
}

/**
 * What became of an order the API accepted (202). The order itself is created in the background by the Order Worker,
 * and the database is the source of truth, so this view follows the processing task until it is finished
 * (`Succeeded` / `Failed`):
 * - while the realtime connection is live, by push: a hint for the task invalidates its cache tag and the task is
 *   fetched again (`features/realtime`); nothing is asked in between;
 * - while it is not, by polling: every 1.5 s, and not while the tab is hidden.
 * Either way it waits for at most 60 s, then says so and offers "Check again" (one request, another 60 s). The task and
 * the window live in the cache and in this component, so a connection that is lost or comes back while waiting changes
 * how the task is followed, not what is shown. A finished task that produced an order loads that order once (status
 * and total amount).
 *
 * When the task is first seen finished, the lists it changed (orders, stock, movements, products) are invalidated
 * once - not on every poll.
 */
export function OrderOutcome({ slug, channelLabel, accepted, onNewOrder }: OrderOutcomeProps) {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  const dispatch = useAppDispatch();
  const { taskId } = accepted;

  const live = useAppSelector(selectIsLive);

  const [waitUntil, setWaitUntil] = useState(accepted.acceptedAt + POLL_WINDOW_MS);
  const [windowOver, setWindowOver] = useState(false);

  // State and subscription are separate hooks because the subscription's polling depends on the state it produces.
  const {
    data: task,
    error: taskError,
    isError: taskFailed,
    isFetching: checking,
  } = processingApi.endpoints.getProcessingTask.useQueryState(taskId);
  const finished = task !== undefined && isTerminalTaskStatus(task.status);
  const waiting = !finished && !windowOver;
  const { refetch } = processingApi.endpoints.getProcessingTask.useQuerySubscription(taskId, {
    // Live: the hint for this task refetches it. Not live: ask.
    pollingInterval: waiting && !live ? POLL_INTERVAL_MS : 0,
    skipPollingIfUnfocused: true,
    // A resubmitted order shows the task as it is now, not as an earlier outcome view left it in the cache.
    refetchOnMountOrArgChange: true,
  });

  const orderId = task?.status === 'Succeeded' ? task.orderId : null;
  const order = useGetOrderQuery(orderId ?? skipToken);

  useEffect(() => {
    if (!waiting) {
      return;
    }

    // A timer only: in push mode nothing is requested while it runs.
    const timer = window.setTimeout(() => setWindowOver(true), Math.max(0, waitUntil - Date.now()));
    return () => window.clearTimeout(timer);
  }, [waiting, waitUntil]);

  const announced = useRef(false);
  useEffect(() => {
    if (finished && !announced.current) {
      announced.current = true;
      // The order list, not single orders: the order of this task is loaded right now and would be loaded twice.
      dispatch(baseApi.util.invalidateTags([ORDER_LIST_TAG, 'Inventory', 'InventoryMovement', 'Product']));
    }
  }, [dispatch, finished]);

  const checkAgain = () => {
    setWaitUntil(Date.now() + POLL_WINDOW_MS);
    setWindowOver(false);
    void refetch();
  };

  const orderLoading = Boolean(orderId) && !order.data && !order.isError;
  const state = task ? describeState(t, task, order.data, orderLoading) : undefined;
  // What the user can act on (e.g. the task is gone); network errors and 5xx are reported globally.
  const taskErrors = taskFailed && !task ? problemMessages(t, taskError) : [];
  const orderErrors = order.isError ? problemMessages(t, order.error) : [];

  return (
    <div data-testid={`order-outcome-${slug}`}>
      <Descriptions
        bordered
        size="small"
        column={1}
        styles={{ label: { width: SUMMARY_LABEL_WIDTH } }}
        style={{ marginBottom: 16 }}
        items={[
          {
            key: 'externalOrderId',
            label: t('orderEntry.form.externalOrderId'),
            children: accepted.externalOrderId,
          },
          { key: 'channel', label: t('orderEntry.outcome.channel'), children: channelLabel },
          {
            key: 'taskStatus',
            label: t('orderEntry.outcome.taskStatus'),
            children: task ? <TaskStatusTag status={task.status} /> : <Spin size="small" />,
          },
          ...(order.data
            ? [
                {
                  key: 'orderStatus',
                  label: t('orderEntry.outcome.orderStatus'),
                  children: <OrderStatusTag status={order.data.status} />,
                },
                {
                  key: 'totalAmount',
                  label: t('orderEntry.outcome.totalAmount'),
                  children: formatMoney(order.data.totalAmount),
                },
              ]
            : []),
        ]}
      />

      <Flex vertical gap={8} style={{ marginBottom: 16 }}>
        {accepted.duplicate && <Alert type="info" showIcon title={t('orderEntry.outcome.duplicate')} />}
        {state && <Alert type={state.type} showIcon title={state.title} description={state.description} />}
        {[...taskErrors, ...orderErrors].length > 0 && (
          <Alert type="error" showIcon title={[...taskErrors, ...orderErrors].join(' ')} />
        )}
        {!finished && windowOver && (
          <Alert
            type="info"
            showIcon
            title={t('orderEntry.outcome.stillProcessing')}
            action={
              <Button loading={checking} onClick={checkAgain} style={{ width: SECONDARY_ACTION_WIDTH }}>
                {t('orderEntry.outcome.checkAgain')}
              </Button>
            }
          />
        )}
      </Flex>

      <Space size={16}>
        <Button onClick={onNewOrder} style={{ width: PAGE_ACTION_WIDTH }}>
          {t('orderEntry.outcome.newOrder')}
        </Button>
        {orderId && (
          <Link to={orderPath(orderId)} style={{ color: token.colorLink }}>
            {t('orderEntry.outcome.openOrder')}
          </Link>
        )}
      </Space>
    </div>
  );
}
