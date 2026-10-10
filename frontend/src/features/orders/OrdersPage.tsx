import { EyeOutlined, FileExcelOutlined, ReloadOutlined } from '@ant-design/icons';
import { Alert, Button, DatePicker, Input, Select, Space, Table, theme, Tooltip, type TableProps } from 'antd';
import dayjs from 'dayjs';
import { useCallback, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useLocation, useNavigate } from 'react-router';
import { useHasAnyRole } from '@/features/auth/useHasAnyRole';
import { useListChannelsQuery } from '@/features/channels/channelsApi';
import { CustomerSelect } from '@/features/customers/components/CustomerSelect';
import { exportPrefillState, REPORTS_PATH, REPORTS_ROLES } from '@/features/reports/paths';
import { problemMessages } from '@/shared/api/problemDetails';
import {
  DATE_RANGE_WIDTH,
  FILTER_WIDTH,
  PAGE_ACTION_WIDTH,
  PRODUCT_SELECT_WIDTH,
  SEARCH_WIDTH,
  SECONDARY_ACTION_WIDTH,
  SINGLE_ACTION_COLUMN_WIDTH,
} from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useListQueryState } from '@/shared/hooks/useListQueryState';
import { oneOf } from '@/shared/utils/address';
import { readLocalDayRange } from '@/shared/utils/dateRange';
import { API_DATE_FORMAT, DATE_FORMAT, formatDateTime, formatMoney, formatNumber } from '@/shared/utils/format';
import { isGuid } from '@/shared/utils/guid';
import { OrderStatusTag } from './components/OrderStatusTag';
import { PaymentStatusTag } from './components/PaymentStatusTag';
import {
  ORDER_SORT_FIELDS,
  ORDER_STATUSES,
  useListOrdersQuery,
  type OrderChannel,
  type OrderCustomer,
  type OrderStatus,
  type OrderSummary,
  type PaymentStatus,
} from './ordersApi';
import { orderPath, type OrderDetailLocationState } from './paths';

/** Filters of this page in the address (next to `page` / `pageSize` / `search` / `sortBy` / `sortDir`). */
const FILTER_PARAMS = ['channel', 'status', 'customerId', 'from', 'to'] as const;

/**
 * Sum of the column widths without the action column. The columns users scan (created, order id, status, payment,
 * total: 810 px) come first and fit into the content area of the smallest supported window (1280 px wide: 992 px, 976
 * next to a scrollbar); the rest scrolls.
 */
const COLUMNS_WIDTH = 1270;

/**
 * Orders (every role), newest first unless the user sorts. Paging, sorting, search and filters live in the address;
 * values there that are not valid (an unknown status, a customer id that is no GUID, a bad date) are ignored and never
 * sent. The channel slug is sent as it is: the API matches an unknown slug to nothing. The date range is a range of
 * local days (see `dateRange.ts`).
 *
 * Orders are created in the background by the Order Worker, so the list is fetched again whenever the page is opened
 * and on "Refresh", and when a realtime hint says that an order was created or changed (a payment).
 */
export function OrdersPage() {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  const navigate = useNavigate();
  const location = useLocation();
  const canExport = useHasAnyRole(REPORTS_ROLES);
  const list = useListQueryState({
    sortFields: ORDER_SORT_FIELDS,
    defaultSortField: 'createdAt',
    // Without a sort in the address the API answers newest first, and the table says so.
    defaultSortDir: 'desc',
  });
  const { searchParams, patchParams, sortOrder } = list;

  const channel = searchParams.get('channel')?.trim() || undefined;
  const status = oneOf(ORDER_STATUSES, searchParams.get('status'));
  const rawCustomerId = searchParams.get('customerId');
  const customerId = isGuid(rawCustomerId) ? rawCustomerId : undefined;
  const range = readLocalDayRange(searchParams);

  const { data, error, isError, isFetching, refetch } = useListOrdersQuery(
    {
      page: list.page,
      pageSize: list.pageSize,
      search: list.search,
      sortBy: list.sortBy,
      sortDir: list.sortDir,
      channel,
      status,
      customerId,
      from: range.fromInstant,
      to: range.toInstant,
    },
    { refetchOnMountOrArgChange: true },
  );
  const channels = useListChannelsQuery();

  /** A changed filter starts on the first page. */
  const setFilter = useCallback(
    (changes: Partial<Record<(typeof FILTER_PARAMS)[number], string | undefined>>) =>
      patchParams({ ...changes, page: undefined }),
    [patchParams],
  );

  // Where "Back" on a detail page returns to: this list as it is now (page, sort, filters).
  const detailState = useMemo<OrderDetailLocationState>(
    () => ({ from: `${location.pathname}${location.search}` }),
    [location.pathname, location.search],
  );

  const columns = useMemo<TableProps<OrderSummary>['columns']>(
    () => [
      {
        key: 'createdAt',
        dataIndex: 'createdAt',
        title: t('orders.columns.createdAt'),
        width: 160,
        ellipsis: true,
        sorter: true,
        // Starts descending (the default order), so the next click asks for ascending.
        sortDirections: ['descend', 'ascend'],
        sortOrder: sortOrder('createdAt'),
        render: (createdAt: string) => formatDateTime(createdAt),
      },
      {
        key: 'externalOrderId',
        dataIndex: 'externalOrderId',
        title: t('orders.columns.externalOrderId'),
        width: 210,
        ellipsis: { showTitle: false },
        sorter: true,
        sortOrder: sortOrder('externalOrderId'),
        render: (externalOrderId: string, order) => (
          <Tooltip title={externalOrderId}>
            <Link
              to={orderPath(order.orderId)}
              state={detailState}
              style={{
                display: 'block',
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
                color: token.colorLink,
              }}
            >
              {externalOrderId}
            </Link>
          </Tooltip>
        ),
      },
      {
        key: 'status',
        dataIndex: 'status',
        title: t('orders.columns.status'),
        width: 160,
        ellipsis: true,
        render: (orderStatus: OrderStatus) => <OrderStatusTag status={orderStatus} />,
      },
      {
        key: 'paymentStatus',
        dataIndex: 'paymentStatus',
        title: t('orders.columns.payment'),
        width: 130,
        ellipsis: true,
        render: (paymentStatus: PaymentStatus) => <PaymentStatusTag status={paymentStatus} />,
      },
      {
        key: 'totalAmount',
        dataIndex: 'totalAmount',
        title: t('orders.columns.totalAmount'),
        width: 150,
        align: 'right',
        ellipsis: true,
        sorter: true,
        sortOrder: sortOrder('totalAmount'),
        render: (totalAmount: number) => formatMoney(totalAmount),
      },
      {
        key: 'customer',
        dataIndex: 'customer',
        title: t('orders.columns.customer'),
        width: 200,
        ellipsis: { showTitle: false },
        render: (customer: OrderCustomer) => <TruncatedText text={customer.name} />,
      },
      {
        key: 'channel',
        dataIndex: 'channel',
        title: t('orders.columns.channel'),
        width: 160,
        ellipsis: { showTitle: false },
        render: (orderChannel: OrderChannel) => <TruncatedText text={orderChannel.name} />,
      },
      {
        key: 'itemCount',
        dataIndex: 'itemCount',
        title: t('orders.columns.itemCount'),
        width: 100,
        align: 'right',
        ellipsis: true,
        render: (itemCount: number) => formatNumber(itemCount),
      },
      {
        key: 'actions',
        title: t('common.actions'),
        width: SINGLE_ACTION_COLUMN_WIDTH,
        ellipsis: true,
        render: (_: unknown, order) => (
          <Tooltip title={t('orders.actions.detail')}>
            <Button
              type="text"
              icon={<EyeOutlined />}
              aria-label={t('orders.actions.detail')}
              onClick={() => void navigate(orderPath(order.orderId), { state: detailState })}
            />
          </Tooltip>
        ),
      },
    ],
    [detailState, navigate, sortOrder, t, token.colorLink],
  );

  // What the user can fix (a filter the API rejects) is shown above the table instead of an empty list; network errors
  // and 5xx are reported globally.
  const errorMessages = isError ? problemMessages(t, error) : [];

  return (
    <>
      <PageHeader
        title={t('orders.title')}
        extra={
          <Space>
            <Button
              icon={<ReloadOutlined />}
              loading={isFetching}
              onClick={() => void refetch()}
              style={{ width: SECONDARY_ACTION_WIDTH }}
            >
              {t('common.refresh')}
            </Button>
            {/* Hidden for roles that may not export; the API enforces the same rule. */}
            {canExport && (
              <Button
                icon={<FileExcelOutlined />}
                // To the Reports page with the filters of this list (not its paging or sort): the form there is filled
                // in, and nothing is requested before the user submits it.
                onClick={() =>
                  void navigate(REPORTS_PATH, {
                    state: exportPrefillState({
                      reportType: 'Orders',
                      channel,
                      status,
                      customerId,
                      search: list.search,
                      from: range.from,
                      to: range.to,
                    }),
                  })
                }
                style={{ width: PAGE_ACTION_WIDTH }}
              >
                {t('reports.exportToExcel')}
              </Button>
            )}
          </Space>
        }
      />
      <Space wrap style={{ marginBottom: 16 }}>
        <Input.Search
          allowClear
          value={list.searchText}
          placeholder={t('orders.searchPlaceholder')}
          aria-label={t('orders.searchPlaceholder')}
          onChange={(event) => list.setSearchText(event.target.value)}
          style={{ width: SEARCH_WIDTH }}
        />
        <Select<string>
          allowClear
          value={channel}
          placeholder={t('orders.filters.channel')}
          aria-label={t('orders.filters.channel')}
          loading={channels.isFetching}
          options={(channels.data ?? []).map((item) => ({ value: item.slug, label: item.name }))}
          onChange={(value) => setFilter({ channel: value })}
          style={{ width: FILTER_WIDTH }}
        />
        <Select<OrderStatus>
          allowClear
          value={status}
          placeholder={t('orders.filters.status')}
          aria-label={t('orders.filters.status')}
          options={ORDER_STATUSES.map((value) => ({ value, label: t(`orders.statuses.${value}`) }))}
          onChange={(value) => setFilter({ status: value })}
          style={{ width: FILTER_WIDTH }}
        />
        <CustomerSelect
          allowClear
          value={customerId}
          placeholder={t('orders.filters.customer')}
          aria-label={t('orders.filters.customer')}
          onChange={(value) => setFilter({ customerId: value })}
          style={{ width: PRODUCT_SELECT_WIDTH }}
        />
        <DatePicker.RangePicker
          allowEmpty={[true, true]}
          format={DATE_FORMAT}
          value={[range.from ? dayjs(range.from) : null, range.to ? dayjs(range.to) : null]}
          placeholder={[t('orders.filters.from'), t('orders.filters.to')]}
          onChange={(picked) =>
            setFilter({ from: picked?.[0]?.format(API_DATE_FORMAT), to: picked?.[1]?.format(API_DATE_FORMAT) })
          }
          style={{ width: DATE_RANGE_WIDTH }}
        />
        <Button
          onClick={() => setFilter(Object.fromEntries(FILTER_PARAMS.map((name) => [name, undefined])))}
          style={{ width: SECONDARY_ACTION_WIDTH }}
        >
          {t('orders.filters.clear')}
        </Button>
      </Space>
      {errorMessages.length > 0 && (
        <Alert type="error" showIcon title={errorMessages.join(' ')} style={{ marginBottom: 16 }} />
      )}
      <Table<OrderSummary>
        rowKey="orderId"
        columns={columns}
        dataSource={isError ? [] : data?.items}
        loading={isFetching}
        tableLayout="fixed"
        scroll={{ x: COLUMNS_WIDTH + SINGLE_ACTION_COLUMN_WIDTH }}
        sortDirections={['ascend', 'descend']}
        onChange={list.onTableChange}
        pagination={list.pagination(isError ? 0 : data?.totalCount)}
      />
    </>
  );
}
