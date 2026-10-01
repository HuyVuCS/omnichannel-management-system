import { Flex } from 'antd';
import { useTranslation } from 'react-i18next';
import { useListChannelsQuery } from '@/features/channels/channelsApi';
import { PageHeader } from '@/shared/components/PageHeader';
import { OrderEntryPanel } from './components/OrderEntryPanel';
import { ORDER_ENTRY_PANELS } from './panels';

/**
 * Order entry (every role): three panels on one page, each sending its orders to its own channel
 * (`POST /api/orders/{slug}`). The panels are independent and their state lives only as long as the page is open.
 */
export function OrderEntryPage() {
  const { t } = useTranslation();
  const channels = useListChannelsQuery();

  return (
    <>
      <PageHeader title={t('orderEntry.title')} />
      <Flex vertical gap={16}>
        {ORDER_ENTRY_PANELS.map((panel) => (
          <OrderEntryPanel
            key={panel.slug}
            panel={panel}
            channel={channels.data?.find((channel) => channel.slug === panel.slug)}
            channelsKnown={channels.isSuccess}
          />
        ))}
      </Flex>
    </>
  );
}
