import { Card, Space, Tag, Typography } from 'antd';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Channel } from '@/features/channels/channelsApi';
import type { OrderEntryPanelConfig } from '../panels';
import { OrderEntryForm, type AcceptedOrder } from './OrderEntryForm';
import { OrderOutcome } from './OrderOutcome';

interface OrderEntryPanelProps {
  panel: OrderEntryPanelConfig;
  /** The channel with the panel's slug, when the channel list contains it. */
  channel: Channel | undefined;
  /** False while the channel list is not known yet (loading, or it could not be loaded): no warning is shown then. */
  channelsKnown: boolean;
}

/**
 * One order-entry panel: the order form of its channel, and after the API accepted an order (202) what became of it,
 * until the user starts a new order. Form, request and outcome belong to this panel alone; the other panels are not
 * touched by anything that happens here.
 *
 * The channel's name and status are shown as the channel list reports them. An inactive or missing channel is a warning
 * only - the form stays usable, because whether the channel takes orders is the API's decision
 * (404 `channel_not_found`), not this page's.
 */
export function OrderEntryPanel({ panel, channel, channelsKnown }: OrderEntryPanelProps) {
  const { t } = useTranslation();
  const [accepted, setAccepted] = useState<AcceptedOrder | null>(null);
  const title = t(panel.titleKey);
  // The channel's name is shown next to the panel's name only when it says something else ("Website Website").
  const channelNameDiffers =
    channel !== undefined && channel.name.trim().toLocaleLowerCase() !== title.trim().toLocaleLowerCase();

  return (
    <Card
      data-testid={`order-panel-${panel.slug}`}
      title={
        <Space size={12}>
          <span>{title}</span>
          {channel && channelNameDiffers && (
            <Typography.Text type="secondary" style={{ fontWeight: 'normal' }}>
              {channel.name}
            </Typography.Text>
          )}
          {channel?.status === 'Inactive' && <Tag color="warning">{t('orderEntry.channel.inactive')}</Tag>}
          {channelsKnown && !channel && <Tag color="warning">{t('orderEntry.channel.notConfigured')}</Tag>}
        </Space>
      }
    >
      {accepted ? (
        <OrderOutcome
          slug={panel.slug}
          channelLabel={channel?.name ?? title}
          accepted={accepted}
          // A new form instance: empty, with a new external order id.
          onNewOrder={() => setAccepted(null)}
        />
      ) : (
        <OrderEntryForm slug={panel.slug} idPrefix={panel.idPrefix} onAccepted={setAccepted} />
      )}
    </Card>
  );
}
