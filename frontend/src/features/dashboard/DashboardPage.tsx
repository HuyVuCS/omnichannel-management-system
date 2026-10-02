import { ReloadOutlined } from '@ant-design/icons';
import {
  Button,
  Card,
  Col,
  DatePicker,
  Flex,
  Row,
  Segmented,
  Space,
  Statistic,
  Table,
  theme,
  Tooltip,
  Typography,
  type TableProps,
} from 'antd';
import dayjs from 'dayjs';
import relativeTime from 'dayjs/plugin/relativeTime';
import { useMemo, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router';
import { lowStockPath } from '@/features/inventory/paths';
import { OrderStatusTag } from '@/features/orders/components/OrderStatusTag';
import { processingListPath } from '@/features/processing/paths';
import type { TaskStatus } from '@/features/processing/processingApi';
import { problemMessages } from '@/shared/api/problemDetails';
import {
  DASHBOARD_PRESETS_WIDTH,
  DASHBOARD_UPDATED_WIDTH,
  DATE_RANGE_WIDTH,
  SECONDARY_ACTION_WIDTH,
} from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { API_DATE_FORMAT, DATE_FORMAT, formatDateTime, formatMoney, formatNumber } from '@/shared/utils/format';
import {
  useGetDashboardSummaryQuery,
  type DashboardChannelOrders,
  type DashboardOrderStatusCount,
  type DashboardProcessing,
  type DashboardStockSync,
} from './dashboardApi';
import { DEFAULT_PRESET, RANGE_PARAM, RANGE_PRESETS, readDashboardRange, type RangePreset } from './range';

dayjs.extend(relativeTime);

const NUMBER_COLUMN_WIDTH = 130;

/** A number column whose title says "(in the range)": the title must fit in both languages. */
const RANGE_COLUMN_WIDTH = 200;

/** A number that has a list behind it: a link when there is something to look at. */
function CountLink({ count, to }: { count: number; to: string }) {
  const { token } = theme.useToken();

  return count === 0 ? (
    formatNumber(count)
  ) : (
    <Link to={to} style={{ color: token.colorLink }}>
      {formatNumber(count)}
    </Link>
  );
}

interface RelativeTimeProps {
  instant: string;
  /** When the summary was generated: the moment `instant` is compared with (not this browser's clock). */
  generatedAt: string;
  /** "5 minutes" instead of "5 minutes ago". */
  withoutSuffix?: boolean;
}

/** A moment as "5 minutes ago", with the exact time as a tooltip. The words follow the UI language (dayjs locale). */
function RelativeTime({ instant, generatedAt, withoutSuffix = false }: RelativeTimeProps) {
  return (
    <Tooltip title={formatDateTime(instant)}>
      <span>{dayjs(instant).from(dayjs(generatedAt), withoutSuffix)}</span>
    </Tooltip>
  );
}

/** One figure of a card: the server's value under its label, shown as it is (no number formatting of AntD's own). */
function Figure({ title, children }: { title: string; children: ReactNode }) {
  return <Statistic title={title} value="" formatter={() => children} />;
}

/**
 * Overview (every role): the numbers of `GET /api/dashboard/summary` as the server computed them. Nothing is added,
 * compared or judged here - no totals, no thresholds, no limit on the range (a range the server rejects is shown on
 * the range control). Non-zero numbers that have a list behind them link to it with the matching filters.
 *
 * The range lives in the address: a preset (`?range=today|7d|30d`; the default, the last 24 hours, writes nothing and
 * sends nothing) or a custom range of local days (`?from=…&to=…`).
 *
 * The page never polls: realtime hints invalidate its cache tag (at most one refetch per second, `features/realtime`),
 * it is fetched again whenever it is opened, and "Refresh" asks at once.
 */
export function DashboardPage() {
  const { t } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();
  const range = readDashboardRange(searchParams);

  // `currentData`: while another range is loading - or was rejected - the numbers of the previous one are not shown.
  const {
    currentData: summary,
    error,
    isError,
    isFetching,
    refetch,
  } = useGetDashboardSummaryQuery(range.request, { refetchOnMountOrArgChange: true });

  const setRange = (next: { preset?: RangePreset; from?: string; to?: string }) =>
    setSearchParams(
      (previous) => {
        const params = new URLSearchParams(previous);
        for (const [key, value] of [
          [RANGE_PARAM, next.preset === DEFAULT_PRESET ? undefined : next.preset],
          ['from', next.from],
          ['to', next.to],
        ] as const) {
          if (value) {
            params.set(key, value);
          } else {
            params.delete(key);
          }
        }

        return params;
      },
      { replace: true },
    );

  const statusColumns = useMemo<TableProps<DashboardOrderStatusCount>['columns']>(
    () => [
      {
        key: 'status',
        dataIndex: 'status',
        title: t('dashboard.orders.status'),
        ellipsis: true,
        render: (status: DashboardOrderStatusCount['status']) => <OrderStatusTag status={status} />,
      },
      {
        key: 'count',
        dataIndex: 'count',
        title: t('dashboard.orders.count'),
        width: NUMBER_COLUMN_WIDTH,
        align: 'right',
        ellipsis: true,
        render: (count: number) => formatNumber(count),
      },
    ],
    [t],
  );

  const channelColumns = useMemo<TableProps<DashboardChannelOrders>['columns']>(() => {
    const count = (value: number) => formatNumber(value);
    return [
      {
        key: 'name',
        dataIndex: 'name',
        title: t('dashboard.orders.channel'),
        ellipsis: { showTitle: false },
        render: (name: string) => <TruncatedText text={name} />,
      },
      {
        key: 'total',
        dataIndex: 'total',
        title: t('dashboard.orders.channelTotal'),
        width: NUMBER_COLUMN_WIDTH,
        align: 'right',
        ellipsis: true,
        render: count,
      },
      {
        key: 'reserved',
        dataIndex: 'reserved',
        title: t('dashboard.orders.channelReserved'),
        width: NUMBER_COLUMN_WIDTH,
        align: 'right',
        ellipsis: true,
        render: count,
      },
      {
        key: 'insufficientStock',
        dataIndex: 'insufficientStock',
        title: t('dashboard.orders.channelInsufficientStock'),
        width: NUMBER_COLUMN_WIDTH,
        align: 'right',
        ellipsis: true,
        render: count,
      },
    ];
  }, [t]);

  const processingColumns = useMemo<TableProps<DashboardProcessing>['columns']>(() => {
    /** `finished`: the number counts tasks that reached the status in the range - the list shows them newest first. */
    const statusColumn = (
      key: Exclude<keyof DashboardProcessing, 'taskType'>,
      status: TaskStatus,
      finished = false,
    ) => ({
      key,
      dataIndex: key,
      title: t(`dashboard.processing.${key}`),
      width: finished ? RANGE_COLUMN_WIDTH : NUMBER_COLUMN_WIDTH,
      align: 'right' as const,
      ellipsis: true,
      render: (count: number, row: DashboardProcessing) => (
        <CountLink
          count={count}
          to={processingListPath({
            taskType: row.taskType,
            status,
            ...(finished && { sortBy: 'updatedAt', sortDir: 'desc' }),
          })}
        />
      ),
    });

    return [
      {
        key: 'taskType',
        dataIndex: 'taskType',
        title: t('dashboard.processing.taskType'),
        ellipsis: { showTitle: false },
        render: (taskType: DashboardProcessing['taskType']) => (
          <TruncatedText text={t(`processing.taskTypes.${taskType}`, taskType)} />
        ),
      },
      statusColumn('pending', 'Pending'),
      statusColumn('inProgress', 'InProgress'),
      statusColumn('retryScheduled', 'RetryScheduled'),
      statusColumn('failed', 'Failed', true),
      statusColumn('succeeded', 'Succeeded', true),
    ];
  }, [t]);

  const generatedAt = summary?.generatedAt;
  const stockSyncColumns = useMemo<TableProps<DashboardStockSync>['columns']>(
    () => [
      {
        key: 'name',
        dataIndex: 'name',
        title: t('dashboard.stockSync.channel'),
        ellipsis: { showTitle: false },
        render: (name: string) => <TruncatedText text={name} />,
      },
      {
        key: 'retryScheduled',
        dataIndex: 'retryScheduled',
        title: t('dashboard.stockSync.retryScheduled'),
        width: NUMBER_COLUMN_WIDTH,
        align: 'right',
        ellipsis: true,
        render: (count: number, row) => (
          <CountLink
            count={count}
            to={processingListPath({ taskType: 'StockSync', status: 'RetryScheduled', channelId: row.channelId })}
          />
        ),
      },
      {
        key: 'failed',
        dataIndex: 'failed',
        title: t('dashboard.stockSync.failed'),
        width: RANGE_COLUMN_WIDTH,
        align: 'right',
        ellipsis: true,
        render: (count: number, row) => (
          <CountLink
            count={count}
            to={processingListPath({ taskType: 'StockSync', status: 'Failed', channelId: row.channelId })}
          />
        ),
      },
      {
        key: 'lastSucceededAt',
        dataIndex: 'lastSucceededAt',
        title: t('dashboard.stockSync.lastSucceededAt'),
        width: 220,
        ellipsis: true,
        render: (lastSucceededAt: string | null) =>
          lastSucceededAt && generatedAt ? (
            <RelativeTime instant={lastSucceededAt} generatedAt={generatedAt} />
          ) : (
            t('dashboard.stockSync.never')
          ),
      },
    ],
    [generatedAt, t],
  );

  // What the user can fix - a range the server does not accept - is shown on the range control; network errors and
  // 5xx are reported globally.
  const rangeErrors = isError ? problemMessages(t, error) : [];

  return (
    <>
      <PageHeader
        title={t('dashboard.title')}
        extra={
          <Space>
            <Typography.Text
              type="secondary"
              ellipsis
              data-testid="dashboard-updated"
              style={{ width: DASHBOARD_UPDATED_WIDTH, textAlign: 'end' }}
            >
              {summary ? t('dashboard.updatedAt', { time: dayjs(summary.generatedAt).format('HH:mm:ss') }) : ''}
            </Typography.Text>
            <Button
              icon={<ReloadOutlined />}
              loading={isFetching}
              onClick={() => void refetch()}
              style={{ width: SECONDARY_ACTION_WIDTH }}
            >
              {t('common.refresh')}
            </Button>
          </Space>
        }
      />
      <Space wrap style={{ marginBottom: rangeErrors.length > 0 ? 8 : 16 }}>
        <Segmented<RangePreset>
          block
          aria-label={t('dashboard.range.label')}
          // No preset is marked while a custom range is in use.
          value={range.preset ?? ('' as RangePreset)}
          options={RANGE_PRESETS.map((preset) => ({ value: preset, label: t(`dashboard.range.${preset}`) }))}
          onChange={(preset) => setRange({ preset })}
          style={{ width: DASHBOARD_PRESETS_WIDTH }}
        />
        <DatePicker.RangePicker
          allowEmpty={[true, true]}
          format={DATE_FORMAT}
          status={rangeErrors.length > 0 ? 'error' : undefined}
          value={[range.from ? dayjs(range.from) : null, range.to ? dayjs(range.to) : null]}
          placeholder={[t('dashboard.range.from'), t('dashboard.range.to')]}
          // Picked days are a custom range; a cleared picker is the default again.
          onChange={(picked) =>
            setRange({ from: picked?.[0]?.format(API_DATE_FORMAT), to: picked?.[1]?.format(API_DATE_FORMAT) })
          }
          style={{ width: DATE_RANGE_WIDTH }}
        />
      </Space>
      {rangeErrors.length > 0 && (
        <div role="alert" data-testid="dashboard-range-error" style={{ marginBottom: 16 }}>
          <Typography.Text type="danger">{rangeErrors.join(' ')}</Typography.Text>
        </div>
      )}
      {summary && (
        <Flex vertical gap={16}>
          <Card size="small" title={t('dashboard.orders.title')} data-testid="dashboard-orders">
            <Row gutter={[24, 16]}>
              <Col span={6}>
                <Figure title={t('dashboard.orders.total')}>{formatNumber(summary.orders.total)}</Figure>
              </Col>
              <Col span={18}>
                <Figure title={t('dashboard.orders.salesAmount')}>{formatMoney(summary.orders.salesAmount)}</Figure>
              </Col>
              <Col span={8}>
                <Table<DashboardOrderStatusCount>
                  rowKey="status"
                  size="small"
                  columns={statusColumns}
                  dataSource={summary.orders.byStatus}
                  tableLayout="fixed"
                  pagination={false}
                />
              </Col>
              <Col span={16}>
                <Table<DashboardChannelOrders>
                  rowKey="channelId"
                  size="small"
                  columns={channelColumns}
                  dataSource={summary.orders.byChannel}
                  tableLayout="fixed"
                  pagination={false}
                />
              </Col>
            </Row>
          </Card>

          <Card size="small" title={t('dashboard.inventory.title')} data-testid="dashboard-inventory">
            <Row gutter={24}>
              <Col span={8}>
                <Figure title={t('dashboard.inventory.products')}>{formatNumber(summary.inventory.products)}</Figure>
              </Col>
              <Col span={8}>
                <Figure title={t('dashboard.inventory.lowStock')}>
                  <CountLink count={summary.inventory.lowStock} to={lowStockPath()} />
                </Figure>
              </Col>
              <Col span={8}>
                <Figure title={t('dashboard.inventory.outOfStock')}>
                  {formatNumber(summary.inventory.outOfStock)}
                </Figure>
              </Col>
            </Row>
          </Card>

          <Card size="small" title={t('dashboard.processing.title')} data-testid="dashboard-processing">
            <Table<DashboardProcessing>
              rowKey="taskType"
              size="small"
              columns={processingColumns}
              dataSource={summary.processing}
              tableLayout="fixed"
              pagination={false}
            />
          </Card>

          <Card size="small" title={t('dashboard.stockSync.title')} data-testid="dashboard-stock-sync">
            <Table<DashboardStockSync>
              rowKey="channelId"
              size="small"
              columns={stockSyncColumns}
              dataSource={summary.stockSync}
              tableLayout="fixed"
              pagination={false}
            />
          </Card>

          <Card size="small" title={t('dashboard.outbox.title')} data-testid="dashboard-outbox">
            <Row gutter={24}>
              <Col span={8}>
                <Figure title={t('dashboard.outbox.pending')}>{formatNumber(summary.outbox.pending)}</Figure>
              </Col>
              <Col span={8}>
                <Figure title={t('dashboard.outbox.failed')}>{formatNumber(summary.outbox.failed)}</Figure>
              </Col>
              <Col span={8}>
                <Figure title={t('dashboard.outbox.oldestPending')}>
                  {summary.outbox.oldestPendingAt ? (
                    <RelativeTime
                      instant={summary.outbox.oldestPendingAt}
                      generatedAt={summary.generatedAt}
                      withoutSuffix
                    />
                  ) : (
                    t('dashboard.outbox.none')
                  )}
                </Figure>
              </Col>
            </Row>
          </Card>
        </Flex>
      )}
    </>
  );
}
