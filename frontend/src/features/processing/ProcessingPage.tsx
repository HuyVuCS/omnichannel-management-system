import { EyeOutlined, ReloadOutlined } from '@ant-design/icons';
import { Alert, Button, DatePicker, Input, Select, Space, Table, theme, Tooltip, type TableProps } from 'antd';
import dayjs from 'dayjs';
import { useCallback, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useLocation, useNavigate } from 'react-router';
import { useListChannelsQuery } from '@/features/channels/channelsApi';
import { orderPath } from '@/features/orders/paths';
import { problemMessages } from '@/shared/api/problemDetails';
import {
  DATE_RANGE_WIDTH,
  FILTER_WIDTH,
  SEARCH_WIDTH,
  SECONDARY_ACTION_WIDTH,
  SINGLE_ACTION_COLUMN_WIDTH,
} from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useAddressText } from '@/shared/hooks/useAddressText';
import { useListQueryState } from '@/shared/hooks/useListQueryState';
import { oneOf } from '@/shared/utils/address';
import { readLocalDayRange } from '@/shared/utils/dateRange';
import { API_DATE_FORMAT, DATE_FORMAT, formatDateTime, formatNumber } from '@/shared/utils/format';
import { isGuid } from '@/shared/utils/guid';
import { TaskStatusTag } from './components/TaskStatusTag';
import { processingTaskPath, type TaskDetailLocationState } from './paths';
import {
  PROCESSING_SORT_FIELDS,
  TASK_STATUSES,
  TASK_TYPES,
  useListProcessingTasksQuery,
  type ProcessingAttempt,
  type ProcessingTaskSummary,
  type TaskStatus,
  type TaskType,
} from './processingApi';

/** Filters of this page in the address (next to `page` / `pageSize` / `sortBy` / `sortDir`). */
const FILTER_PARAMS = ['taskType', 'status', 'channelId', 'correlationId', 'errorCode', 'from', 'to'] as const;

/**
 * Sum of the column widths without the action column. Created, type, status, channel and product come first and fit
 * into the content area of the smallest supported window (1280 px wide: 992 px); the attempt columns scroll.
 */
const COLUMNS_WIDTH = 1810;

/**
 * Sync & Processing (every role): every asynchronous task - order processing, stock sync per channel - with its
 * status, its channel and product, and its last attempt: which worker instance ran it and what went wrong. Newest first
 * unless the user sorts. Paging, sorting and filters live in the address; values there that are not valid (an unknown
 * status, a channel id that is no GUID, a bad date) are ignored and never sent. The date range is a range of local days
 * on the creation time (see `dateRange.ts`).
 *
 * Tasks change in the background: the list is fetched again whenever the page is opened, on "Refresh", and when a
 * realtime hint says a task changed (`features/realtime`, at most once per second).
 */
export function ProcessingPage() {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  const navigate = useNavigate();
  const location = useLocation();
  const list = useListQueryState({
    sortFields: PROCESSING_SORT_FIELDS,
    defaultSortField: 'createdAt',
    // Without a sort in the address the API answers newest first, and the table says so.
    defaultSortDir: 'desc',
  });
  const { searchParams, patchParams, sortOrder } = list;

  const taskType = oneOf(TASK_TYPES, searchParams.get('taskType'));
  const status = oneOf(TASK_STATUSES, searchParams.get('status'));
  const rawChannelId = searchParams.get('channelId');
  const channelId = isGuid(rawChannelId) ? rawChannelId : undefined;
  const correlationId = searchParams.get('correlationId')?.trim() || undefined;
  const errorCode = searchParams.get('errorCode')?.trim() || undefined;
  const range = readLocalDayRange(searchParams);

  const { data, error, isError, isFetching, refetch } = useListProcessingTasksQuery(
    {
      page: list.page,
      pageSize: list.pageSize,
      sortBy: list.sortBy,
      sortDir: list.sortDir,
      taskType,
      status,
      channelId,
      correlationId,
      errorCode,
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

  const commitCorrelationId = useCallback((text: string) => setFilter({ correlationId: text }), [setFilter]);
  const [correlationText, setCorrelationText] = useAddressText(correlationId ?? '', commitCorrelationId);
  const commitErrorCode = useCallback((text: string) => setFilter({ errorCode: text }), [setFilter]);
  const [errorCodeText, setErrorCodeText] = useAddressText(errorCode ?? '', commitErrorCode);

  // Where "Back" on a detail page returns to: this list as it is now (page, sort, filters).
  const detailState = useMemo<TaskDetailLocationState>(
    () => ({ from: `${location.pathname}${location.search}` }),
    [location.pathname, location.search],
  );

  /** Channel names come from the channel list; a channel it does not contain is shown by its id. */
  const channelNames = useMemo(
    () => new Map((channels.data ?? []).map((channel) => [channel.channelId, channel.name])),
    [channels.data],
  );

  const columns = useMemo<TableProps<ProcessingTaskSummary>['columns']>(
    () => [
      {
        key: 'createdAt',
        dataIndex: 'createdAt',
        title: t('processing.columns.createdAt'),
        width: 160,
        ellipsis: true,
        sorter: true,
        // Starts descending (the default order), so the next click asks for ascending.
        sortDirections: ['descend', 'ascend'],
        sortOrder: sortOrder('createdAt'),
        render: (createdAt: string, task) => (
          <Link to={processingTaskPath(task.taskId)} state={detailState} style={{ color: token.colorLink }}>
            {formatDateTime(createdAt)}
          </Link>
        ),
      },
      {
        key: 'taskType',
        dataIndex: 'taskType',
        title: t('processing.columns.taskType'),
        width: 160,
        ellipsis: { showTitle: false },
        render: (type: TaskType) => <TruncatedText text={t(`processing.taskTypes.${type}`, type)} />,
      },
      {
        key: 'status',
        dataIndex: 'status',
        title: t('processing.columns.status'),
        width: 140,
        ellipsis: true,
        render: (taskStatus: TaskStatus) => <TaskStatusTag status={taskStatus} />,
      },
      {
        key: 'channelId',
        dataIndex: 'channelId',
        title: t('processing.columns.channel'),
        width: 160,
        ellipsis: { showTitle: false },
        render: (id: string | null) => <TruncatedText text={id ? (channelNames.get(id) ?? id) : null} />,
      },
      {
        key: 'productSku',
        dataIndex: 'productSku',
        title: t('processing.columns.productSku'),
        width: 200,
        ellipsis: { showTitle: false },
        render: (sku: string | null) => <TruncatedText text={sku} />,
      },
      {
        key: 'orderId',
        dataIndex: 'orderId',
        title: t('processing.columns.order'),
        width: 110,
        ellipsis: true,
        render: (orderId: string | null) =>
          orderId && (
            <Link to={orderPath(orderId)} style={{ color: token.colorLink }}>
              {t('processing.openOrder')}
            </Link>
          ),
      },
      {
        key: 'attemptCount',
        dataIndex: 'attemptCount',
        title: t('processing.columns.attemptCount'),
        width: 120,
        align: 'right',
        ellipsis: true,
        render: (attemptCount: number) => formatNumber(attemptCount),
      },
      {
        key: 'lastErrorCode',
        dataIndex: 'lastAttempt',
        title: t('processing.columns.lastErrorCode'),
        width: 180,
        ellipsis: { showTitle: false },
        render: (lastAttempt: ProcessingAttempt | null) => <TruncatedText text={lastAttempt?.errorCode} />,
      },
      {
        key: 'lastWorkerInstance',
        dataIndex: 'lastAttempt',
        title: t('processing.columns.lastWorkerInstance'),
        width: 200,
        ellipsis: { showTitle: false },
        render: (lastAttempt: ProcessingAttempt | null) => <TruncatedText text={lastAttempt?.workerInstanceId} />,
      },
      {
        key: 'lastErrorMessage',
        dataIndex: 'lastAttempt',
        title: t('processing.columns.lastErrorMessage'),
        width: 220,
        ellipsis: { showTitle: false },
        // Sanitised by the API: safe to show.
        render: (lastAttempt: ProcessingAttempt | null) => <TruncatedText text={lastAttempt?.errorMessage} />,
      },
      {
        key: 'updatedAt',
        dataIndex: 'updatedAt',
        title: t('processing.columns.updatedAt'),
        width: 160,
        ellipsis: true,
        sorter: true,
        sortDirections: ['descend', 'ascend'],
        sortOrder: sortOrder('updatedAt'),
        render: (updatedAt: string | null) => formatDateTime(updatedAt),
      },
      {
        key: 'actions',
        title: t('common.actions'),
        width: SINGLE_ACTION_COLUMN_WIDTH,
        ellipsis: true,
        render: (_: unknown, task) => (
          <Tooltip title={t('processing.actions.detail')}>
            <Button
              type="text"
              icon={<EyeOutlined />}
              aria-label={t('processing.actions.detail')}
              onClick={() => void navigate(processingTaskPath(task.taskId), { state: detailState })}
            />
          </Tooltip>
        ),
      },
    ],
    [channelNames, detailState, navigate, sortOrder, t, token.colorLink],
  );

  // What the user can fix (a filter the API rejects) is shown above the table instead of an empty list; network errors
  // and 5xx are reported globally.
  const errorMessages = isError ? problemMessages(t, error) : [];

  return (
    <>
      <PageHeader
        title={t('processing.title')}
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
      <Space wrap style={{ marginBottom: 16 }}>
        <Select<TaskType>
          allowClear
          value={taskType}
          placeholder={t('processing.filters.taskType')}
          aria-label={t('processing.filters.taskType')}
          options={TASK_TYPES.map((value) => ({ value, label: t(`processing.taskTypes.${value}`) }))}
          onChange={(value) => setFilter({ taskType: value })}
          style={{ width: FILTER_WIDTH }}
        />
        <Select<TaskStatus>
          allowClear
          value={status}
          placeholder={t('processing.filters.status')}
          aria-label={t('processing.filters.status')}
          options={TASK_STATUSES.map((value) => ({ value, label: t(`processing.taskStatuses.${value}`) }))}
          onChange={(value) => setFilter({ status: value })}
          style={{ width: FILTER_WIDTH }}
        />
        <Select<string>
          allowClear
          value={channelId}
          placeholder={t('processing.filters.channel')}
          aria-label={t('processing.filters.channel')}
          loading={channels.isFetching}
          options={(channels.data ?? []).map((channel) => ({ value: channel.channelId, label: channel.name }))}
          onChange={(value) => setFilter({ channelId: value })}
          style={{ width: FILTER_WIDTH }}
        />
        <DatePicker.RangePicker
          allowEmpty={[true, true]}
          format={DATE_FORMAT}
          value={[range.from ? dayjs(range.from) : null, range.to ? dayjs(range.to) : null]}
          placeholder={[t('processing.filters.from'), t('processing.filters.to')]}
          onChange={(picked) =>
            setFilter({ from: picked?.[0]?.format(API_DATE_FORMAT), to: picked?.[1]?.format(API_DATE_FORMAT) })
          }
          style={{ width: DATE_RANGE_WIDTH }}
        />
        <Input
          allowClear
          value={correlationText}
          placeholder={t('processing.filters.correlationId')}
          aria-label={t('processing.filters.correlationId')}
          onChange={(event) => setCorrelationText(event.target.value)}
          style={{ width: SEARCH_WIDTH }}
        />
        <Input
          allowClear
          value={errorCodeText}
          placeholder={t('processing.filters.errorCode')}
          aria-label={t('processing.filters.errorCode')}
          onChange={(event) => setErrorCodeText(event.target.value)}
          style={{ width: FILTER_WIDTH }}
        />
        <Button
          onClick={() => setFilter(Object.fromEntries(FILTER_PARAMS.map((name) => [name, undefined])))}
          style={{ width: SECONDARY_ACTION_WIDTH }}
        >
          {t('processing.filters.clear')}
        </Button>
      </Space>
      {errorMessages.length > 0 && (
        <Alert type="error" showIcon title={errorMessages.join(' ')} style={{ marginBottom: 16 }} />
      )}
      <Table<ProcessingTaskSummary>
        rowKey="taskId"
        columns={columns}
        dataSource={isError ? [] : data?.items}
        loading={isFetching}
        tableLayout="fixed"
        scroll={{ x: COLUMNS_WIDTH + SINGLE_ACTION_COLUMN_WIDTH }}
        sortDirections={['descend', 'ascend']}
        onChange={list.onTableChange}
        pagination={list.pagination(isError ? 0 : data?.totalCount)}
      />
    </>
  );
}
