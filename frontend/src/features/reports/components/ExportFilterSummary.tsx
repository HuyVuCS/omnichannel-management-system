import { skipToken } from '@reduxjs/toolkit/query';
import { useTranslation } from 'react-i18next';
import { useListChannelsQuery } from '@/features/channels/channelsApi';
import { useGetCustomerQuery } from '@/features/customers/customersApi';
import { ORDER_STATUSES } from '@/features/orders/ordersApi';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { formatDate } from '@/shared/utils/format';
import type { ReportExport } from '../reportsApi';

/** What the line shows for a name that is not known (yet): the customer is being loaded, or does not exist any more. */
const UNKNOWN = '…';

const text = (value: unknown): string | undefined => (typeof value === 'string' && value !== '' ? value : undefined);

/**
 * What an export was asked for, as one line built from its stored `parameters` (the request as the API normalised
 * it): the range as local days, the channel and the customer by name, the status by its label, the search text.
 * Names are looked up - the parameters hold a channel slug and a customer id: channels from the channel list (one
 * cached request), a customer from `GET /api/customers/{id}` (one request per customer, however many rows name it).
 * A value this client cannot name is shown as it is.
 */
export function ExportFilterSummary({ item }: { item: ReportExport }) {
  const { t } = useTranslation();
  const parameters = item.parameters ?? {};
  const channelSlug = item.reportType === 'Orders' ? text(parameters.channel) : undefined;
  const customerId = item.reportType === 'Orders' ? text(parameters.customerId) : undefined;
  const channels = useListChannelsQuery(undefined, { skip: channelSlug === undefined });
  const customer = useGetCustomerQuery(customerId ?? skipToken);
  const search = text(parameters.search);
  const parts: string[] = [];

  if (item.reportType === 'Orders') {
    const from = formatDate(text(parameters.from));
    const to = formatDate(text(parameters.to));
    if (from && to) {
      parts.push(`${from} – ${to}`);
    }

    if (channelSlug) {
      const name = channels.data?.find((channel) => channel.slug === channelSlug)?.name ?? channelSlug;
      parts.push(t('reports.filters.channel', { name }));
    }

    const status = text(parameters.status);
    if (status) {
      const label = (ORDER_STATUSES as readonly string[]).includes(status) ? t(`orders.statuses.${status}`) : status;
      parts.push(t('reports.filters.status', { status: label }));
    }

    if (customerId) {
      parts.push(t('reports.filters.customer', { name: customer.data?.name ?? UNKNOWN }));
    }

    if (search) {
      parts.push(t('reports.filters.search', { text: search }));
    }
  } else {
    if (search) {
      parts.push(t('reports.filters.search', { text: search }));
    }

    if (parameters.lowStockOnly === true) {
      parts.push(t('reports.filters.lowStockOnly'));
    }

    if (parts.length === 0) {
      parts.push(t('reports.filters.allProducts'));
    }
  }

  return <TruncatedText text={parts.join(' · ')} />;
}
