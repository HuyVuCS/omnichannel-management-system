import { CheckOutlined } from '@ant-design/icons';
import { App as AntApp, Button, Tooltip } from 'antd';
import { useTranslation } from 'react-i18next';
import { problemMessages } from '@/shared/api/problemDetails';
import { useAcknowledgeAlertMutation } from '../alertsApi';

/**
 * "Acknowledge" for an open alert: "seen", nothing more - the alert stays until the system finds its condition gone.
 * It cannot be undone, so it asks first (the same pattern as the manual retry of a task). The caller shows it to
 * Manager and Admin on `Open` rows only; the API enforces both. What the API refuses - the alert is resolved already,
 * it does not exist - is said in the UI language, and the list is fetched again (see `acknowledgeAlert`); network
 * errors and 5xx are reported globally.
 */
export function AcknowledgeAlertButton({ alertId }: { alertId: string }) {
  const { t } = useTranslation();
  const { modal, message } = AntApp.useApp();
  const [acknowledge, { isLoading }] = useAcknowledgeAlertMutation();

  const confirmAcknowledge = () => {
    modal.confirm({
      title: t('alerts.acknowledge.confirmTitle'),
      content: t('alerts.acknowledge.confirmContent'),
      okText: t('alerts.acknowledge.action'),
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          await acknowledge(alertId).unwrap();
          message.success(t('alerts.acknowledge.done'));
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
    <Tooltip title={t('alerts.acknowledge.action')}>
      <Button
        type="text"
        size="small"
        icon={<CheckOutlined />}
        aria-label={t('alerts.acknowledge.action')}
        loading={isLoading}
        onClick={confirmAcknowledge}
      />
    </Tooltip>
  );
}
