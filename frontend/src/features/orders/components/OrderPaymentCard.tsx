import { Button, Card, Descriptions, Space, Typography, theme, type DescriptionsProps } from 'antd';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { useHasAnyRole } from '@/features/auth/useHasAnyRole';
import { TaskStatusTag } from '@/features/processing/components/TaskStatusTag';
import { processingTaskPath } from '@/features/processing/paths';
import type { TaskType } from '@/features/processing/processingApi';
import { PAGE_ACTION_WIDTH, SUMMARY_LABEL_WIDTH } from '@/shared/components/layout';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { formatDateTime } from '@/shared/utils/format';
import type { OrderDetail, OrderTask } from '../ordersApi';
import { PAYMENT_ROLES } from '../paths';
import { PaymentStatusTag } from './PaymentStatusTag';
import { PayOrderModal } from './PayOrderModal';

/** What the card shows for a field the order does not have. */
const EMPTY = '—';

/**
 * The widest a payment reference gets on its own row: the content area of the smallest supported window (1280 px)
 * without the label column and the cell padding. A longer reference is truncated, the full text is its tooltip.
 */
const REFERENCE_MAX_WIDTH = 700;

/**
 * The widest the address of who recorded the payment gets: the value cell of a two-column row in that window. A longer
 * address is truncated the same way instead of wrapping.
 */
const PAYER_MAX_WIDTH = 220;

/** The order's task of a type: there is one per order (the keys of the payment step); of several, the newest. */
function taskOf(order: OrderDetail, taskType: TaskType): OrderTask | undefined {
  return order.tasks
    .filter((task) => task.taskType === taskType)
    .reduce<OrderTask | undefined>(
      (newest, task) => (newest && newest.createdAt >= task.createdAt ? newest : task),
      undefined,
    );
}

/**
 * The payment of an order and what follows from it, as three independent facts (every role reads them): the payment
 * itself, the invoice - its number once it is issued, the state of its task until then - and the confirmation e-mail
 * (the state of its task, or that none was sent because the customer had no address). A failed invoice or e-mail
 * changes nothing about the payment or about the other one. Everything is the API's; only the task of each type is
 * picked here. Retrying a failed task stays with the task blocks below and on the task's own page.
 *
 * Manager and Admin record the payment here while the order is `Reserved` and unpaid (the API enforces both).
 */
export function OrderPaymentCard({ order }: { order: OrderDetail }) {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  const canPay = useHasAnyRole(PAYMENT_ROLES);
  const [paying, setPaying] = useState(false);
  const { payment, invoice } = order;
  const paid = payment.status === 'Paid';

  const taskState = (task: OrderTask): ReactNode => (
    <Space>
      <TaskStatusTag status={task.status} />
      <Link to={processingTaskPath(task.taskId)} style={{ color: token.colorLink }}>
        {t('orderDetail.payment.taskLink')}
      </Link>
    </Space>
  );

  const invoiceTask = taskOf(order, 'Invoice');
  const emailTask = taskOf(order, 'Email');

  const items: DescriptionsProps['items'] = [
    {
      key: 'status',
      label: t('orderDetail.payment.status'),
      span: paid ? 1 : 2,
      children: <PaymentStatusTag status={payment.status} />,
    },
  ];
  if (paid) {
    items.push(
      {
        key: 'method',
        label: t('orderDetail.payment.method'),
        children: payment.method ? t(`orders.paymentMethods.${payment.method}`, payment.method) : EMPTY,
      },
      { key: 'paidAt', label: t('orderDetail.payment.paidAt'), children: formatDateTime(payment.paidAt) || EMPTY },
      {
        key: 'paidBy',
        label: t('orderDetail.payment.paidBy'),
        children: payment.paidByEmail ? (
          <div style={{ maxWidth: PAYER_MAX_WIDTH }}>
            <TruncatedText text={payment.paidByEmail} />
          </div>
        ) : (
          EMPTY
        ),
      },
      {
        key: 'reference',
        label: t('orderDetail.payment.reference'),
        span: 2,
        children: payment.reference ? (
          <div style={{ maxWidth: REFERENCE_MAX_WIDTH }}>
            <TruncatedText text={payment.reference} />
          </div>
        ) : (
          EMPTY
        ),
      },
      {
        key: 'invoice',
        label: t('orderDetail.payment.invoice'),
        span: 2,
        // The number and when it was issued on one row: the number with its copy button needs the room of both columns.
        children: invoice ? (
          <Space size="middle">
            <Typography.Text code copyable>
              {invoice.number}
            </Typography.Text>
            <Typography.Text type="secondary">
              {t('orderDetail.payment.invoiceIssuedAt', { at: formatDateTime(invoice.issuedAt) })}
            </Typography.Text>
          </Space>
        ) : invoiceTask ? (
          taskState(invoiceTask)
        ) : (
          EMPTY
        ),
      },
      {
        key: 'email',
        label: t('orderDetail.payment.email'),
        span: 2,
        children: emailTask ? taskState(emailTask) : t('orderDetail.payment.noEmail'),
      },
    );
  }

  const payable = order.status === 'Reserved';

  return (
    <Card
      title={t('orderDetail.payment.title')}
      size="small"
      data-testid="order-payment"
      extra={
        !paid &&
        canPay &&
        payable && (
          <Button type="primary" size="small" onClick={() => setPaying(true)} style={{ width: PAGE_ACTION_WIDTH }}>
            {t('orderDetail.payment.markPaid')}
          </Button>
        )
      }
    >
      <Descriptions bordered size="small" column={2} styles={{ label: { width: SUMMARY_LABEL_WIDTH } }} items={items} />
      {!paid && canPay && !payable && (
        <Typography.Text type="secondary" style={{ display: 'block', marginTop: 12 }}>
          {t('orderDetail.payment.notPayable')}
        </Typography.Text>
      )}
      {/* Hidden for roles that may not pay; the API enforces the same rule. */}
      {canPay && <PayOrderModal order={order} open={paying} onClose={() => setPaying(false)} />}
    </Card>
  );
}
