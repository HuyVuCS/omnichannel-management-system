import { Tag } from 'antd';
import { useTranslation } from 'react-i18next';
import type { OrderStatus } from '../ordersApi';

/** One status colour per order status, the same on every page. */
const STATUS_COLORS: Record<OrderStatus, string> = {
  Pending: 'default',
  Reserved: 'processing',
  InsufficientStock: 'warning',
  Cancelled: 'error',
  Completed: 'success',
};

/** The status of an order as the API reports it; a value this client does not know yet is shown as it is. */
export function OrderStatusTag({ status }: { status: OrderStatus }) {
  const { t } = useTranslation();

  return (
    <Tag color={STATUS_COLORS[status] ?? 'default'} style={{ marginInlineEnd: 0 }}>
      {t(`orders.statuses.${status}`, status)}
    </Tag>
  );
}
