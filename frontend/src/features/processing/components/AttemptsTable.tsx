import { Table, Tag, type TableProps } from 'antd';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useKeyboardScrollableTable } from '@/shared/hooks/useKeyboardScrollableTable';
import { formatDateTime } from '@/shared/utils/format';
import type { AttemptStatus, ProcessingAttempt } from '../processingApi';

/** One status colour per attempt status. */
const STATUS_COLORS: Record<AttemptStatus, string> = {
  Running: 'processing',
  Succeeded: 'success',
  Failed: 'error',
};

/** The error codes of an attempt this client has a label for (`processing.errorCodes.*`); any other is shown as it is. */
const LABELLED_ERROR_CODES: readonly string[] = ['stale_no_progress'];

/**
 * The attempts of one processing task, in the order the API sends them (by number): which worker instance ran it, when,
 * and what went wrong. Long values are one truncated line with the full text as a tooltip. The error message is the one
 * the API sanitised for display.
 */
export function AttemptsTable({ attempts, label }: { attempts: ProcessingAttempt[]; label?: string }) {
  const { t } = useTranslation();
  // The table is wider than its card and its rows hold nothing to tab to.
  const scrollRegion = useKeyboardScrollableTable(label ?? t('processing.detail.attempts.title'));

  const columns = useMemo<TableProps<ProcessingAttempt>['columns']>(
    () => [
      {
        key: 'attemptNo',
        dataIndex: 'attemptNo',
        title: t('processing.attempts.attemptNo'),
        width: 80,
        align: 'right',
        ellipsis: true,
      },
      {
        key: 'status',
        dataIndex: 'status',
        title: t('processing.attempts.status'),
        width: 130,
        ellipsis: true,
        render: (status: AttemptStatus) => (
          <Tag color={STATUS_COLORS[status] ?? 'default'}>{t(`processing.attemptStatuses.${status}`, status)}</Tag>
        ),
      },
      {
        key: 'workerName',
        dataIndex: 'workerName',
        title: t('processing.attempts.workerName'),
        width: 160,
        ellipsis: { showTitle: false },
        render: (workerName: string) => <TruncatedText text={workerName} />,
      },
      {
        key: 'workerInstanceId',
        dataIndex: 'workerInstanceId',
        title: t('processing.attempts.workerInstanceId'),
        width: 220,
        ellipsis: { showTitle: false },
        render: (workerInstanceId: string) => <TruncatedText text={workerInstanceId} />,
      },
      {
        key: 'errorCode',
        dataIndex: 'errorCode',
        title: t('processing.attempts.errorCode'),
        width: 170,
        ellipsis: { showTitle: false },
        render: (errorCode: string | null) => (
          <TruncatedText
            text={
              errorCode && LABELLED_ERROR_CODES.includes(errorCode)
                ? t(`processing.errorCodes.${errorCode}`)
                : errorCode
            }
          />
        ),
      },
      {
        key: 'errorMessage',
        dataIndex: 'errorMessage',
        title: t('processing.attempts.errorMessage'),
        width: 280,
        ellipsis: { showTitle: false },
        render: (errorMessage: string | null) => <TruncatedText text={errorMessage} />,
      },
      {
        key: 'startedAt',
        dataIndex: 'startedAt',
        title: t('processing.attempts.startedAt'),
        width: 160,
        ellipsis: true,
        render: (startedAt: string) => formatDateTime(startedAt),
      },
      {
        key: 'finishedAt',
        dataIndex: 'finishedAt',
        title: t('processing.attempts.finishedAt'),
        width: 160,
        ellipsis: true,
        render: (finishedAt: string | null) => formatDateTime(finishedAt),
      },
    ],
    [t],
  );

  return (
    <Table<ProcessingAttempt>
      ref={scrollRegion}
      rowKey="attemptNo"
      size="small"
      columns={columns}
      dataSource={attempts}
      tableLayout="fixed"
      scroll={{ x: 1360 }}
      pagination={false}
    />
  );
}
