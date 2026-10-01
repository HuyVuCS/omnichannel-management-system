import { App as AntApp, Typography } from 'antd';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { apiErrorBus } from '@/shared/api/apiErrorBus';
import { translateApiError } from '@/shared/api/problemDetails';

const NOTIFICATION_KEY = 'api-error';

/**
 * Shows unexpected API errors (network failure, 5xx) of any request as a notification with the correlation id, which
 * the user can copy when reporting the problem. One notification at a time: a newer error replaces the older one.
 */
export function ApiErrorNotifier() {
  const { notification } = AntApp.useApp();
  const { t } = useTranslation();

  useEffect(
    () =>
      apiErrorBus.subscribe((error) => {
        notification.error({
          key: NOTIFICATION_KEY,
          duration: 0,
          message: t('errors.unexpectedTitle'),
          description: (
            <>
              <div>{translateApiError(t, error)}</div>
              <div>
                {t('errors.correlationId')}{' '}
                <Typography.Text code copyable data-testid="correlation-id">
                  {error.correlationId}
                </Typography.Text>
              </div>
              <Typography.Text type="secondary">{t('errors.correlationHint')}</Typography.Text>
            </>
          ),
        });
      }),
    [notification, t],
  );

  return null;
}
