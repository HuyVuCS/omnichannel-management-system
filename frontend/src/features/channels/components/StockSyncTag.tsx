import { CheckCircleOutlined, MinusCircleOutlined } from '@ant-design/icons';
import { Tag, Tooltip } from 'antd';
import { useTranslation } from 'react-i18next';

/**
 * Whether stock is pushed to a channel at all, as the API says it (`stockSyncEnabled`): icon and text, not colour
 * alone. "Not synced" explains itself on hover: such a channel can have product mappings, but nothing is sent to it.
 */
export function StockSyncTag({ enabled }: { enabled: boolean }) {
  const { t } = useTranslation();

  if (enabled) {
    return (
      <Tag icon={<CheckCircleOutlined />} color="success" style={{ marginInlineEnd: 0 }}>
        {t('channels.stockSync.enabled')}
      </Tag>
    );
  }

  return (
    <Tooltip title={t('channels.stockSync.disabledHint')}>
      <Tag icon={<MinusCircleOutlined />} style={{ marginInlineEnd: 0 }}>
        {t('channels.stockSync.disabled')}
      </Tag>
    </Tooltip>
  );
}
