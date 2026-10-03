import { Space, theme, Tooltip, Typography } from 'antd';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { orderPath } from '@/features/orders/paths';
import { processingTaskPath } from '@/features/processing/paths';
import { formatNumber } from '@/shared/utils/format';
import type { Alert } from '../alertsApi';

/** What the cell shows when the alert has nothing for it. */
const EMPTY = '—';

/**
 * What an alert is about, from the response only:
 * - `LowStock`: the product's available stock and threshold as they are **now** (the API's values; for a resolved
 *   alert they describe the present, not the time of the alert - the tooltip says so);
 * - `InsufficientStock`: the latest order that was short;
 * - `StockSyncFailed`, `ProcessingFailed`: the error code of the latest failure (the API sends no message) and its
 *   task; for a processing failure also the order, when the task has one.
 */
export function AlertDetails({ alert }: { alert: Alert }) {
  const { t } = useTranslation();
  const { token } = theme.useToken();

  const orderLink = alert.orderId && (
    <Link to={orderPath(alert.orderId)} style={{ color: token.colorLink }}>
      {t('alerts.details.order')}
    </Link>
  );

  switch (alert.type) {
    case 'LowStock':
      return alert.availableStock === null || alert.lowStockThreshold === null ? (
        EMPTY
      ) : (
        <Tooltip title={t('alerts.details.lowStockNow')}>
          <span>
            {t('alerts.details.lowStock', {
              available: formatNumber(alert.availableStock),
              threshold: formatNumber(alert.lowStockThreshold),
            })}
          </span>
        </Tooltip>
      );

    case 'InsufficientStock':
      return orderLink || EMPTY;

    case 'StockSyncFailed':
    case 'ProcessingFailed': {
      const taskLink = alert.taskId && (
        <Link to={processingTaskPath(alert.taskId)} style={{ color: token.colorLink }}>
          {t('alerts.details.task')}
        </Link>
      );
      const failureOrder = alert.type === 'ProcessingFailed' && orderLink;
      if (!alert.errorCode && !taskLink && !failureOrder) {
        return EMPTY;
      }

      return (
        <Space size={[8, 0]} wrap>
          {alert.errorCode && (
            <Typography.Text code ellipsis={{ tooltip: alert.errorCode }} style={{ maxWidth: '100%' }}>
              {alert.errorCode}
            </Typography.Text>
          )}
          {taskLink}
          {failureOrder}
        </Space>
      );
    }

    default:
      return EMPTY;
  }
}
