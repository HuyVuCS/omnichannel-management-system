import { DownloadOutlined, EyeOutlined, ReloadOutlined } from '@ant-design/icons';
import { Alert, App as AntApp, Button, Flex, Select, Space, Table, Tooltip, Typography, type TableProps } from 'antd';
import { useCallback, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { processingTaskPath } from '@/features/processing/paths';
import { selectIsLive } from '@/features/realtime/realtimeSlice';
import { problemMessages, translateApiError } from '@/shared/api/problemDetails';
import { isApiError, isUnexpectedError } from '@/shared/api/types';
import { FILTER_WIDTH, SECONDARY_ACTION_WIDTH } from '@/shared/components/layout';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useAppSelector } from '@/shared/hooks/store';
import { useListQueryState } from '@/shared/hooks/useListQueryState';
import { oneOf } from '@/shared/utils/address';
import { formatDateTime, formatFileSize, formatNumber } from '@/shared/utils/format';
import { followDownloadLink } from '../download';
import { REPORT_TYPE_PARAM } from '../paths';
import {
  EXPORT_SORT_FIELDS,
  isUnfinishedExport,
  REPORT_TYPES,
  reportsApi,
  useGetExportDownloadMutation,
  type ExportStatus,
  type ReportExport,
  type ReportType,
} from '../reportsApi';
import { ExportFilterSummary } from './ExportFilterSummary';
import { ExportStatusTag } from './ExportStatusTag';

/** How often the list is asked for while an export on it is not finished and the realtime connection is not live. */
export const EXPORTS_POLL_INTERVAL_MS = 5000;

/** The error codes of a failed export this client has a label for (`reports.errorCodes.*`); any other is shown as it is. */
const ERROR_CODES = ['export_too_large', 'export_expired', 'export_parameters_invalid', 'storage_unavailable'];

/**
 * Column widths. With the action column they are as wide as the content area of the smallest supported window (1280 px
 * wide: 992 px, minus a vertical scrollbar), so a row is readable without horizontal scrolling; the action column is
 * fixed to the right all the same, so what can be done with a row stays in view in a narrower window.
 */
const WIDTHS = { createdAt: 136, reportType: 92, filters: 172, status: 124, rowCount: 76, size: 100, expiresAt: 136 };
const COLUMNS_WIDTH = Object.values(WIDTHS).reduce((sum, width) => sum + width, 0);
const ACTIONS_WIDTH = 140;

/**
 * "My exports": what the signed-in user asked for, newest first (the API shows an export to its requester only). Page,
 * page size, sort direction and the report filter live in the address.
 *
 * A row changes while its export is built. While the realtime connection is live, a hint for the export's task makes
 * the list stale and it is fetched again (`features/realtime`); while it is not, and only as long as an export on the
 * page is not finished, the list is asked for every 5 s (not while the tab is hidden). Opening the page and "Refresh"
 * always ask.
 *
 * Download: the API signs a link for a few minutes; it is followed at once and kept nowhere (`followDownloadLink`,
 * the mutation is reset). A failed export names its error code and links to its task, where the attempts and the
 * retry are (one retry path for every task); an expired one offers nothing.
 */
export function ExportsTable() {
  const { t } = useTranslation();
  const { message } = AntApp.useApp();
  const navigate = useNavigate();
  const live = useAppSelector(selectIsLive);
  const list = useListQueryState({
    sortFields: EXPORT_SORT_FIELDS,
    defaultSortField: 'createdAt',
    // Without a sort in the address the API answers newest first, and the table says so.
    defaultSortDir: 'desc',
  });
  const { searchParams, patchParams, sortOrder } = list;
  const reportType = oneOf(REPORT_TYPES, searchParams.get(REPORT_TYPE_PARAM));

  const args = useMemo(
    () => ({ page: list.page, pageSize: list.pageSize, sortBy: list.sortBy, sortDir: list.sortDir, reportType }),
    [list.page, list.pageSize, list.sortBy, list.sortDir, reportType],
  );
  // State and subscription are separate hooks because the subscription's polling depends on the state it produces.
  const { data, error, isError, isFetching } = reportsApi.endpoints.listReportExports.useQueryState(args);
  const unfinished = (data?.items ?? []).some((item) => isUnfinishedExport(item.status));
  const { refetch } = reportsApi.endpoints.listReportExports.useQuerySubscription(args, {
    // Live: a hint for the export's task refetches the list. Not live: ask, while there is something to wait for.
    pollingInterval: unfinished && !live ? EXPORTS_POLL_INTERVAL_MS : 0,
    skipPollingIfUnfocused: true,
    refetchOnMountOrArgChange: true,
  });

  const [getDownload] = useGetExportDownloadMutation();
  const [downloading, setDownloading] = useState<string | null>(null);
  const download = useCallback(
    async (taskId: string) => {
      setDownloading(taskId);
      const request = getDownload(taskId);
      try {
        const link = await request.unwrap();
        followDownloadLink(link.url);
      } catch (failure) {
        // The export is not what the row says any more (expired, gone): say so and show the row as it is now.
        // Network errors and 5xx are reported globally.
        if (isApiError(failure) && !isUnexpectedError(failure)) {
          message.error(translateApiError(t, failure));
          void refetch();
        }
      } finally {
        // The link is a credential: the answer of this request does not stay in the store.
        request.reset();
        setDownloading(null);
      }
    },
    [getDownload, message, refetch, t],
  );

  const columns = useMemo<TableProps<ReportExport>['columns']>(
    () => [
      {
        key: 'createdAt',
        dataIndex: 'createdAt',
        title: t('reports.columns.createdAt'),
        width: WIDTHS.createdAt,
        ellipsis: true,
        sorter: true,
        sortOrder: sortOrder('createdAt'),
        render: (createdAt: string) => formatDateTime(createdAt),
      },
      {
        key: 'reportType',
        dataIndex: 'reportType',
        title: t('reports.columns.reportType'),
        width: WIDTHS.reportType,
        ellipsis: { showTitle: false },
        render: (type: ReportType) => <TruncatedText text={t(`reports.types.${type}`, type)} />,
      },
      {
        key: 'filters',
        title: t('reports.columns.filters'),
        width: WIDTHS.filters,
        ellipsis: { showTitle: false },
        render: (_: unknown, item) => <ExportFilterSummary item={item} />,
      },
      {
        key: 'status',
        dataIndex: 'status',
        title: t('reports.columns.status'),
        width: WIDTHS.status,
        ellipsis: true,
        render: (status: ExportStatus) => <ExportStatusTag status={status} />,
      },
      {
        key: 'rowCount',
        dataIndex: 'rowCount',
        title: t('reports.columns.rowCount'),
        width: WIDTHS.rowCount,
        align: 'right',
        ellipsis: true,
        render: (rowCount: number | null) => (rowCount === null ? '' : formatNumber(rowCount)),
      },
      {
        key: 'sizeBytes',
        dataIndex: 'sizeBytes',
        title: t('reports.columns.size'),
        width: WIDTHS.size,
        align: 'right',
        ellipsis: true,
        render: (sizeBytes: number | null) => (sizeBytes === null ? '' : formatFileSize(sizeBytes)),
      },
      {
        key: 'expiresAt',
        dataIndex: 'expiresAt',
        title: t('reports.columns.expiresAt'),
        width: WIDTHS.expiresAt,
        ellipsis: true,
        render: (expiresAt: string) => formatDateTime(expiresAt),
      },
      {
        key: 'actions',
        title: t('common.actions'),
        width: ACTIONS_WIDTH,
        fixed: 'right',
        render: (_: unknown, item) => {
          if (item.status === 'Succeeded') {
            return (
              <Button
                size="small"
                icon={<DownloadOutlined />}
                loading={downloading === item.taskId}
                // One link at a time.
                disabled={downloading !== null && downloading !== item.taskId}
                onClick={() => void download(item.taskId)}
              >
                {t('reports.actions.download')}
              </Button>
            );
          }

          if (item.status === 'Failed') {
            const code = item.errorCode ?? '';
            return (
              <Flex align="center" gap={4}>
                <span style={{ flex: '1 1 auto', minWidth: 0, display: 'flex' }}>
                  <TruncatedText text={ERROR_CODES.includes(code) ? t(`reports.errorCodes.${code}`) : code} />
                </span>
                <Tooltip title={t('reports.actions.openTask')}>
                  <Button
                    type="text"
                    size="small"
                    icon={<EyeOutlined />}
                    aria-label={t('reports.actions.openTask')}
                    onClick={() => void navigate(processingTaskPath(item.taskId))}
                  />
                </Tooltip>
              </Flex>
            );
          }

          // Not finished: the status says it. Expired: nothing is left to do with it.
          return null;
        },
      },
    ],
    [download, downloading, navigate, sortOrder, t],
  );

  // What the user can fix (a filter the API rejects) is shown above the table instead of an empty list; network errors
  // and 5xx are reported globally.
  const errorMessages = isError ? problemMessages(t, error) : [];

  return (
    <section aria-labelledby="reports-exports-title">
      <Flex align="center" gap={16} wrap style={{ marginBottom: 16 }}>
        <Typography.Title id="reports-exports-title" level={5} style={{ margin: 0, flex: '1 1 auto', minWidth: 0 }}>
          {t('reports.exports.title')}
        </Typography.Title>
        <Space>
          <Select<ReportType>
            allowClear
            value={reportType}
            placeholder={t('reports.exports.filterType')}
            aria-label={t('reports.exports.filterType')}
            options={REPORT_TYPES.map((value) => ({ value, label: t(`reports.types.${value}`) }))}
            // A changed filter starts on the first page.
            onChange={(value) => patchParams({ [REPORT_TYPE_PARAM]: value, page: undefined })}
            style={{ width: FILTER_WIDTH }}
          />
          <Button
            icon={<ReloadOutlined />}
            loading={isFetching}
            onClick={() => void refetch()}
            style={{ width: SECONDARY_ACTION_WIDTH }}
          >
            {t('common.refresh')}
          </Button>
        </Space>
      </Flex>
      {errorMessages.length > 0 && (
        <Alert type="error" showIcon title={errorMessages.join(' ')} style={{ marginBottom: 16 }} />
      )}
      <Table<ReportExport>
        rowKey="taskId"
        size="small"
        columns={columns}
        dataSource={isError ? [] : data?.items}
        loading={isFetching}
        tableLayout="fixed"
        scroll={{ x: COLUMNS_WIDTH + ACTIONS_WIDTH }}
        // Starts descending (the default order), so the next click asks for ascending.
        sortDirections={['descend', 'ascend']}
        onChange={list.onTableChange}
        pagination={list.pagination(isError ? 0 : data?.totalCount)}
      />
    </section>
  );
}
