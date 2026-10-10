import { Tag } from 'antd';
import { useTranslation } from 'react-i18next';
import type { PaymentStatus } from '../ordersApi';

/** One status colour per payment status, the same on every page. */
const STATUS_COLORS: Record<PaymentStatus, string> = {
  Unpaid: 'default',
  Paid: 'success',
};

/** The payment status of an order as the API reports it; a value this client does not know yet is shown as it is. */
export function PaymentStatusTag({ status }: { status: PaymentStatus }) {
  const { t } = useTranslation();

  return (
    <Tag color={STATUS_COLORS[status] ?? 'default'} style={{ marginInlineEnd: 0 }}>
      {t(`orders.paymentStatuses.${status}`, status)}
    </Tag>
  );
}
