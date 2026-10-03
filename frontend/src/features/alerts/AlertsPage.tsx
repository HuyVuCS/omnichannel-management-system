import { ImportOutlined, ReloadOutlined } from '@ant-design/icons';
import { Alert as AntAlert, Button, DatePicker, Select, Space, Table, theme, Tooltip, Typography } from 'antd';
import type { TableProps } from 'antd';
import dayjs from 'dayjs';
import { useCallback, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate } from 'react-router';
import { useHasAnyRole } from '@/features/auth/useHasAnyRole';
import { useListChannelsQuery } from '@/features/channels/channelsApi';
import { channelPath } from '@/features/channels/paths';
import { importPath, STOCK_IMPORT_ROLES } from '@/features/inventory/paths';
import { ProductSelect } from '@/features/products/components/ProductSelect';
import { productLabel } from '@/features/products/productsApi';
import { problemMessages } from '@/shared/api/problemDetails';
import { isApiError } from '@/shared/api/types';
import {
  DATE_RANGE_WIDTH,
  FILTER_WIDTH,
  MULTI_FILTER_WIDTH,
  PRODUCT_SELECT_WIDTH,
  SECONDARY_ACTION_WIDTH,
  SINGLE_ACTION_COLUMN_WIDTH,
} from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useListQueryState } from '@/shared/hooks/useListQueryState';
import { someOf } from '@/shared/utils/address';
import { readLocalDayRange } from '@/shared/utils/dateRange';
import { API_DATE_FORMAT, DATE_FORMAT, formatDateTime, formatNumber } from '@/shared/utils/format';
import { isGuid } from '@/shared/utils/guid';
import {
  ALERT_SORT_FIELDS,
  ALERT_STATUSES,
  ALERT_TYPES,
  useListAlertsQuery,
  type Alert,
  type AlertStatus,
  type AlertType,
} from './alertsApi';
import { AcknowledgeAlertButton } from './components/AcknowledgeAlertButton';
import { AlertDetails } from './components/AlertDetails';
import { AlertStatusTag } from './components/AlertStatusTag';
import { AlertTypeTag } from './components/AlertTypeTag';
import { ACKNOWLEDGE_ROLES, STATUS_PARAM, TYPE_PARAM } from './paths';

/** Filters of this page in the address (next to `page` / `pageSize` / `sortBy` / `sortDir`). */
const FILTER_PARAMS = [STATUS_PARAM, TYPE_PARAM, 'productId', 'channelId', 'from', 'to'] as const;
type FilterParam = (typeof FILTER_PARAMS)[number];

/** What a cell shows when the alert has no value for it. */
const EMPTY = '—';

/** How many chosen values a multi-value filter shows before "+ n": its width is fixed. */
const MAX_FILTER_TAGS = 2;

/**
 * Column widths. Type, status, product, channel, details and the last occurrence come first: together with the action
 * column (fixed to the right edge) they are as wide as the content area of the smallest supported window (1280 px wide:
 * 992 px, minus a vertical scrollbar), so what an alert is and what can be done about it is visible without horizontal
 * scrolling; the history columns scroll.
 */
const WIDTHS = {
  type: 150,
  status: 126,
  product: 180,
  channel: 110,
  details: 182,
  lastOccurredAt: 140,
  occurrenceCount: 96,
  firstOccurredAt: 140,
  acknowledged: 220,
  resolvedAt: 140,
} as const;
const COLUMNS_WIDTH = Object.values(WIDTHS).reduce((sum, width) => sum + width, 0);

/**
 * Alerts (every role): what needs attention now - low stock, orders rejected for lack of stock, stock syncs and other
 * tasks that failed for good - one row per alert, with the links to what it is about. Most recent occurrence first
 * unless the user sorts. Paging, sorting and filters live in the address; values there that are not valid (an unknown
 * status, an id that is no GUID, a bad date) are ignored and never sent. The date range is a range of local days on the
 * last occurrence (see `dateRange.ts`).
 *
 * No rule is decided here. Which alerts exist and when they are resolved is the server's; **without a status filter
 * the request carries no status and the server shows what is unresolved** (open and acknowledged). Manager and Admin
 * acknowledge an open alert ("seen"; it stays until the system resolves it) and get the shortcut to the stock import
 * for an unresolved stock alert; the API enforces the roles.
 *
 * Alerts change in the background: the list is fetched again whenever the page is opened, on "Refresh", and when a
 * realtime hint says an alert or the stock changed (`features/realtime`, at most once per second). There is no toast,
 * sound or badge for a new alert.
 */
export function AlertsPage() {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  const navigate = useNavigate();
  const canAcknowledge = useHasAnyRole(ACKNOWLEDGE_ROLES);
  const canImport = useHasAnyRole(STOCK_IMPORT_ROLES);
  const list = useListQueryState({
    sortFields: ALERT_SORT_FIELDS,
    defaultSortField: 'lastOccurredAt',
    // Without a sort in the address the API answers most recent occurrence first, and the table says so.
    defaultSortDir: 'desc',
  });
  const { searchParams, patchParams, sortOrder } = list;

  const statuses = someOf(ALERT_STATUSES, searchParams.getAll(STATUS_PARAM));
  const types = someOf(ALERT_TYPES, searchParams.getAll(TYPE_PARAM));
  const rawProductId = searchParams.get('productId');
  const productId = isGuid(rawProductId) ? rawProductId : undefined;
  const rawChannelId = searchParams.get('channelId');
  const channelId = isGuid(rawChannelId) ? rawChannelId : undefined;
  const range = readLocalDayRange(searchParams);

  const { data, error, isError, isFetching, refetch } = useListAlertsQuery(
    {
      page: list.page,
      pageSize: list.pageSize,
      sortBy: list.sortBy,
      sortDir: list.sortDir,
      // No status chosen = no status sent: the default (open and acknowledged) is the server's.
      status: statuses,
      type: types,
      productId,
      channelId,
      from: range.fromInstant,
      to: range.toInstant,
    },
    { refetchOnMountOrArgChange: true },
  );
  const channels = useListChannelsQuery();

  /** A changed filter starts on the first page. */
  const setFilter = useCallback(
    (changes: Partial<Record<FilterParam, string | readonly string[] | undefined>>) =>
      patchParams({ ...changes, page: undefined }),
    [patchParams],
  );

  const columns = useMemo<TableProps<Alert>['columns']>(() => {
    const showActions = canAcknowledge || canImport;

    return [
      {
        key: 'type',
        dataIndex: 'type',
        title: t('alerts.columns.type'),
        width: WIDTHS.type,
        ellipsis: true,
        render: (type: AlertType) => <AlertTypeTag type={type} />,
      },
      {
        key: 'status',
        dataIndex: 'status',
        title: t('alerts.columns.status'),
        width: WIDTHS.status,
        ellipsis: true,
        render: (status: AlertStatus) => <AlertStatusTag status={status} />,
      },
      {
        key: 'product',
        title: t('alerts.columns.product'),
        width: WIDTHS.product,
        ellipsis: { showTitle: false },
        render: (_: unknown, alert) => (
          <TruncatedText
            text={
              alert.productSku && alert.productName
                ? productLabel({ sku: alert.productSku, name: alert.productName })
                : (alert.productSku ?? EMPTY)
            }
          />
        ),
      },
      {
        key: 'channel',
        title: t('alerts.columns.channel'),
        width: WIDTHS.channel,
        ellipsis: { showTitle: false },
        render: (_: unknown, alert) =>
          alert.channelId ? (
            <Tooltip title={alert.channelName ?? alert.channelId}>
              <Link
                to={channelPath(alert.channelId)}
                style={{
                  display: 'block',
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                  color: token.colorLink,
                }}
              >
                {alert.channelName ?? alert.channelId}
              </Link>
            </Tooltip>
          ) : (
            EMPTY
          ),
      },
      {
        key: 'details',
        title: t('alerts.columns.details'),
        width: WIDTHS.details,
        render: (_: unknown, alert) => <AlertDetails alert={alert} />,
      },
      {
        key: 'lastOccurredAt',
        dataIndex: 'lastOccurredAt',
        title: t('alerts.columns.lastOccurredAt'),
        width: WIDTHS.lastOccurredAt,
        ellipsis: true,
        sorter: true,
        sortOrder: sortOrder('lastOccurredAt'),
        render: (lastOccurredAt: string) => formatDateTime(lastOccurredAt),
      },
      {
        key: 'occurrenceCount',
        dataIndex: 'occurrenceCount',
        title: t('alerts.columns.occurrenceCount'),
        width: WIDTHS.occurrenceCount,
        align: 'right',
        ellipsis: true,
        sorter: true,
        sortOrder: sortOrder('occurrenceCount'),
        render: (occurrenceCount: number) => formatNumber(occurrenceCount),
      },
      {
        key: 'firstOccurredAt',
        dataIndex: 'firstOccurredAt',
        title: t('alerts.columns.firstOccurredAt'),
        width: WIDTHS.firstOccurredAt,
        ellipsis: true,
        sorter: true,
        sortOrder: sortOrder('firstOccurredAt'),
        render: (firstOccurredAt: string) => formatDateTime(firstOccurredAt),
      },
      {
        key: 'acknowledged',
        title: t('alerts.columns.acknowledged'),
        width: WIDTHS.acknowledged,
        ellipsis: { showTitle: false },
        render: (_: unknown, alert) => (
          <TruncatedText
            text={
              alert.acknowledgedAt
                ? [alert.acknowledgedByEmail, formatDateTime(alert.acknowledgedAt)].filter(Boolean).join(' · ')
                : EMPTY
            }
          />
        ),
      },
      {
        key: 'resolvedAt',
        dataIndex: 'resolvedAt',
        title: t('alerts.columns.resolvedAt'),
        width: WIDTHS.resolvedAt,
        ellipsis: true,
        render: (resolvedAt: string | null) => formatDateTime(resolvedAt) || EMPTY,
      },
      // Hidden for roles that may not act; the API enforces the same rules.
      ...(showActions
        ? [
            {
              key: 'actions',
              title: t('common.actions'),
              width: SINGLE_ACTION_COLUMN_WIDTH,
              fixed: 'end' as const,
              render: (_: unknown, alert: Alert) => {
                const productOfStockAlert =
                  (alert.type === 'LowStock' || alert.type === 'InsufficientStock') && alert.status !== 'Resolved'
                    ? alert.productId
                    : null;

                return (
                  <Space size={4}>
                    {canAcknowledge && alert.status === 'Open' && <AcknowledgeAlertButton alertId={alert.alertId} />}
                    {canImport && productOfStockAlert && (
                      <Tooltip title={t('alerts.actions.importStock')}>
                        <Button
                          type="text"
                          size="small"
                          icon={<ImportOutlined />}
                          aria-label={t('alerts.actions.importStock')}
                          onClick={() => void navigate(importPath(productOfStockAlert))}
                        />
                      </Tooltip>
                    )}
                  </Space>
                );
              },
            },
          ]
        : []),
    ];
  }, [canAcknowledge, canImport, navigate, sortOrder, t, token.colorLink]);

  // A range the API rejects (its limit is 92 days) is said on the range control; everything else the user can act on
  // above the table. Network errors and 5xx are reported globally.
  const fieldErrors = isError && isApiError(error) ? error.problem?.errors : undefined;
  const rangeErrors = [...(fieldErrors?.from ?? []), ...(fieldErrors?.to ?? [])];
  const otherErrors = isError ? problemMessages(t, error).filter((message) => !rangeErrors.includes(message)) : [];
  const showActions = canAcknowledge || canImport;

  return (
    <>
      <PageHeader
        title={t('alerts.title')}
        extra={
          <Button
            icon={<ReloadOutlined />}
            loading={isFetching}
            onClick={() => void refetch()}
            style={{ width: SECONDARY_ACTION_WIDTH }}
          >
            {t('common.refresh')}
          </Button>
        }
      />
      <Space wrap style={{ marginBottom: rangeErrors.length > 0 ? 8 : 16 }}>
        <Select<AlertStatus[]>
          mode="multiple"
          allowClear
          showSearch={false}
          maxTagCount={MAX_FILTER_TAGS}
          value={statuses}
          // Nothing chosen is the server's default, and the control says which that is.
          placeholder={t('alerts.filters.statusDefault')}
          aria-label={t('alerts.filters.status')}
          options={ALERT_STATUSES.map((value) => ({ value, label: t(`alerts.statuses.${value}`) }))}
          // Written in the order of the API's enum, whatever the order of choosing: the same choice, the same address.
          onChange={(values) => setFilter({ [STATUS_PARAM]: someOf(ALERT_STATUSES, values) })}
          style={{ width: MULTI_FILTER_WIDTH }}
        />
        <Select<AlertType[]>
          mode="multiple"
          allowClear
          showSearch={false}
          maxTagCount={MAX_FILTER_TAGS}
          value={types}
          placeholder={t('alerts.filters.type')}
          aria-label={t('alerts.filters.type')}
          options={ALERT_TYPES.map((value) => ({ value, label: t(`alerts.types.${value}`) }))}
          onChange={(values) => setFilter({ [TYPE_PARAM]: someOf(ALERT_TYPES, values) })}
          style={{ width: MULTI_FILTER_WIDTH }}
        />
        <ProductSelect
          allowClear
          value={productId}
          placeholder={t('alerts.filters.product')}
          aria-label={t('alerts.filters.product')}
          onChange={(value) => setFilter({ productId: value })}
          style={{ width: PRODUCT_SELECT_WIDTH }}
        />
        <Select<string>
          allowClear
          value={channelId}
          placeholder={t('alerts.filters.channel')}
          aria-label={t('alerts.filters.channel')}
          loading={channels.isFetching}
          options={(channels.data ?? []).map((channel) => ({ value: channel.channelId, label: channel.name }))}
          onChange={(value) => setFilter({ channelId: value })}
          style={{ width: FILTER_WIDTH }}
        />
        <DatePicker.RangePicker
          allowEmpty={[true, true]}
          format={DATE_FORMAT}
          status={rangeErrors.length > 0 ? 'error' : undefined}
          value={[range.from ? dayjs(range.from) : null, range.to ? dayjs(range.to) : null]}
          placeholder={[t('alerts.filters.from'), t('alerts.filters.to')]}
          onChange={(picked) =>
            setFilter({ from: picked?.[0]?.format(API_DATE_FORMAT), to: picked?.[1]?.format(API_DATE_FORMAT) })
          }
          style={{ width: DATE_RANGE_WIDTH }}
        />
        <Button
          onClick={() => setFilter(Object.fromEntries(FILTER_PARAMS.map((name) => [name, undefined])))}
          style={{ width: SECONDARY_ACTION_WIDTH }}
        >
          {t('alerts.filters.clear')}
        </Button>
      </Space>
      {rangeErrors.length > 0 && (
        <div role="alert" data-testid="alerts-range-error" style={{ marginBottom: 16 }}>
          <Typography.Text type="danger">{rangeErrors.join(' ')}</Typography.Text>
        </div>
      )}
      {otherErrors.length > 0 && (
        <AntAlert type="error" showIcon title={otherErrors.join(' ')} style={{ marginBottom: 16 }} />
      )}
      <Table<Alert>
        rowKey="alertId"
        size="small"
        columns={columns}
        dataSource={isError ? [] : data?.items}
        loading={isFetching}
        tableLayout="fixed"
        scroll={{ x: COLUMNS_WIDTH + (showActions ? SINGLE_ACTION_COLUMN_WIDTH : 0) }}
        // Every sort field starts descending (most recent / most frequent first); the next click asks for ascending.
        sortDirections={['descend', 'ascend']}
        onChange={list.onTableChange}
        pagination={list.pagination(isError ? 0 : data?.totalCount)}
      />
    </>
  );
}
