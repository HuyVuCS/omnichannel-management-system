import { ReloadOutlined } from '@ant-design/icons';
import {
  Alert,
  Button,
  DatePicker,
  Input,
  Segmented,
  Select,
  Space,
  Table,
  theme,
  Tooltip,
  Typography,
  type TableProps,
} from 'antd';
import dayjs from 'dayjs';
import { useCallback, useEffect, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { useHasAnyRole } from '@/features/auth/useHasAnyRole';
import { DEFAULT_PRESET, RANGE_PARAM, RANGE_PRESETS, type RangePreset } from '@/features/dashboard/range';
import { orderPath } from '@/features/orders/paths';
import { processingTaskPath } from '@/features/processing/paths';
import { problemMessages } from '@/shared/api/problemDetails';
import { isApiError } from '@/shared/api/types';
import {
  DASHBOARD_PRESETS_WIDTH,
  DATE_RANGE_WIDTH,
  ID_FILTER_WIDTH,
  MULTI_FILTER_WIDTH,
  SECONDARY_ACTION_WIDTH,
} from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useAddressText } from '@/shared/hooks/useAddressText';
import { useListQueryState } from '@/shared/hooks/useListQueryState';
import { someOf } from '@/shared/utils/address';
import { API_DATE_FORMAT, DATE_FORMAT, formatDateTime } from '@/shared/utils/format';
import { isGuid } from '@/shared/utils/guid';
import { EventLevelTag } from './components/EventLevelTag';
import { eventSummary, eventTypeLabel } from './eventSummary';
import {
  CORRELATION_PARAM,
  EVENT_TYPE_PARAM,
  FILTER_PARAMS,
  LEVEL_PARAM,
  ORDER_PARAM,
  SINCE_PARAM,
  TASK_PARAM,
  tracePath,
  USER_EVENT_ROLES,
} from './paths';
import { newSince, readLogsRange } from './range';
import {
  EVENT_LEVELS,
  EVENT_TYPES,
  SYSTEM_EVENT_SORT_FIELDS,
  USER_EVENT_PREFIX,
  useListSystemEventsQuery,
  type EventLevel,
  type SystemEvent,
} from './systemEventsApi';

/** What a cell shows when the event has no value for it. */
const EMPTY = '—';

/** How many chosen values a multi-value filter shows before "+ n": its width is fixed. */
const MAX_FILTER_TAGS = 2;

/** Everything about the time window in the address. */
const RANGE_PARAMS = [RANGE_PARAM, 'from', 'to', SINCE_PARAM] as const;

/**
 * Column widths. Together they are as wide as the content area of the smallest supported window (1280 px wide: 992 px,
 * minus a vertical scrollbar), so a row is readable without horizontal scrolling; longer texts are cut with a tooltip.
 */
const WIDTHS = { createdAt: 140, level: 126, eventType: 160, summary: 240, who: 130, links: 180 } as const;
const COLUMNS_WIDTH = Object.values(WIDTHS).reduce((sum, width) => sum + width, 0);

const ONE_LINE = { display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' } as const;

/** A filter for one id: what was typed, and the id to ask for when it is one. */
function readId(raw: string | null): { text: string; id?: string; invalid: boolean } {
  const text = raw?.trim() ?? '';
  return { text, id: isGuid(text) ? text : undefined, invalid: text !== '' && !isGuid(text) };
}

/**
 * Logs / audit (Manager and Admin): the business events the system recorded - stock imports, rejected orders, failed
 * tasks and manual retries, acknowledged alerts and, for an Admin, user management - newest first. One row says what
 * happened, who did it and links to the order, the task and the **trace**: every event with the same correlation id,
 * i.e. the whole flow from the request to its processing (architecture doc §5.3).
 *
 * Nothing is decided here. Which events a role may read and which fields of an event are shown is the server's (an
 * allowlist per event type); this page turns what it gets into a line of text. Paging, the sort direction, the filters
 * and the time window live in the address; values there that are not valid are ignored and never sent, except an id
 * that is no GUID: the field says so and nothing is asked. The time window is described in `range.ts`.
 *
 * There is no realtime hint for events: the list is fetched again whenever the page is opened and on "Refresh".
 */
export function LogsPage() {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  const seesUserEvents = useHasAnyRole(USER_EVENT_ROLES);
  const list = useListQueryState({
    sortFields: SYSTEM_EVENT_SORT_FIELDS,
    defaultSortField: 'createdAt',
    // Without a sort in the address the API answers newest first, and the table says so.
    defaultSortDir: 'desc',
  });
  const { searchParams, patchParams, sortOrder } = list;

  // The event types the filter offers; the server enforces who may read which (a hidden type asked for by hand finds
  // nothing).
  const eventTypeOptions = useMemo(
    () => EVENT_TYPES.filter((type) => seesUserEvents || !type.startsWith(USER_EVENT_PREFIX)),
    [seesUserEvents],
  );

  const levels = someOf(EVENT_LEVELS, searchParams.getAll(LEVEL_PARAM));
  const eventTypes = someOf(eventTypeOptions, searchParams.getAll(EVENT_TYPE_PARAM));
  const correlationId = searchParams.get(CORRELATION_PARAM)?.trim() || undefined;
  const order = readId(searchParams.get(ORDER_PARAM));
  const task = readId(searchParams.get(TASK_PARAM));
  const range = readLogsRange(searchParams);

  // The address names no time window (the menu link, "Clear filters", a cleared picker): the default - the last 24
  // hours from now - is written to it, so that a reload asks for the same window.
  const windowMissing = range.request === undefined;
  useEffect(() => {
    if (windowMissing) {
      patchParams({ [SINCE_PARAM]: newSince() });
    }
  }, [windowMissing, patchParams]);

  const idInvalid = order.invalid || task.invalid;
  const { currentData, error, isError, isFetching, refetch } = useListSystemEventsQuery(
    {
      page: list.page,
      pageSize: list.pageSize,
      sortBy: list.sortBy,
      sortDir: list.sortDir,
      level: levels,
      eventType: eventTypes,
      correlationId,
      orderId: order.id,
      taskId: task.id,
      from: range.request?.from,
      to: range.request?.to,
    },
    // Nothing is asked before the window is known, nor with an id the API would reject.
    { refetchOnMountOrArgChange: true, skip: windowMissing || idInvalid },
  );
  const data = idInvalid ? undefined : currentData;

  /** A changed filter starts on the first page. */
  const setFilter = useCallback(
    (changes: Record<string, string | readonly string[] | undefined>) => patchParams({ ...changes, page: undefined }),
    [patchParams],
  );

  /** A preset or a custom range replaces whatever window the address had. */
  const setRange = useCallback(
    (next: { preset?: RangePreset; from?: string; to?: string }) =>
      setFilter({
        [RANGE_PARAM]: next.preset === DEFAULT_PRESET ? undefined : next.preset,
        from: next.from,
        to: next.to,
        // Choosing "last 24 hours" counts them from now.
        [SINCE_PARAM]: next.preset === DEFAULT_PRESET ? newSince() : undefined,
      }),
    [setFilter],
  );

  const commitCorrelationId = useCallback((text: string) => setFilter({ [CORRELATION_PARAM]: text }), [setFilter]);
  const [correlationText, setCorrelationText] = useAddressText(correlationId ?? '', commitCorrelationId);
  const commitOrderId = useCallback((text: string) => setFilter({ [ORDER_PARAM]: text }), [setFilter]);
  const [orderText, setOrderText] = useAddressText(order.text, commitOrderId);
  const commitTaskId = useCallback((text: string) => setFilter({ [TASK_PARAM]: text }), [setFilter]);
  const [taskText, setTaskText] = useAddressText(task.text, commitTaskId);

  const columns = useMemo<TableProps<SystemEvent>['columns']>(() => {
    const linkStyle = { color: token.colorLink };

    return [
      {
        key: 'createdAt',
        dataIndex: 'createdAt',
        title: t('logs.columns.createdAt'),
        width: WIDTHS.createdAt,
        ellipsis: true,
        sorter: true,
        sortOrder: sortOrder('createdAt'),
        render: (createdAt: string) => formatDateTime(createdAt),
      },
      {
        key: 'level',
        dataIndex: 'level',
        title: t('logs.columns.level'),
        width: WIDTHS.level,
        ellipsis: true,
        render: (level: EventLevel) => <EventLevelTag level={level} />,
      },
      {
        key: 'eventType',
        dataIndex: 'eventType',
        title: t('logs.columns.eventType'),
        width: WIDTHS.eventType,
        ellipsis: { showTitle: false },
        render: (eventType: string) => <TruncatedText text={eventTypeLabel(t, eventType)} />,
      },
      {
        key: 'summary',
        title: t('logs.columns.summary'),
        width: WIDTHS.summary,
        ellipsis: { showTitle: false },
        render: (_: unknown, event) => {
          const summary = eventSummary(t, event);
          if (!summary) {
            return EMPTY;
          }

          // The whole line, and what does not fit a line at all: the (sanitised) message of a failure.
          return (
            <Tooltip title={summary.more ? `${summary.text} — ${summary.more}` : summary.text}>
              <span style={ONE_LINE}>{summary.text}</span>
            </Tooltip>
          );
        },
      },
      {
        key: 'who',
        title: t('logs.columns.who'),
        width: WIDTHS.who,
        ellipsis: { showTitle: false },
        render: (_: unknown, event) => (
          <TruncatedText
            text={
              // User management: who did it to whom.
              event.targetEmail ? `${event.actorEmail ?? EMPTY} → ${event.targetEmail}` : (event.actorEmail ?? EMPTY)
            }
          />
        ),
      },
      {
        key: 'links',
        title: t('logs.columns.links'),
        width: WIDTHS.links,
        render: (_: unknown, event) =>
          event.orderId || event.taskId || event.correlationId ? (
            <Space size={[8, 0]} wrap>
              {event.orderId && (
                <Link to={orderPath(event.orderId)} style={linkStyle}>
                  {t('logs.links.order')}
                </Link>
              )}
              {event.taskId && (
                <Link to={processingTaskPath(event.taskId)} style={linkStyle}>
                  {t('logs.links.task')}
                </Link>
              )}
              {event.correlationId && (
                <Tooltip title={t('logs.links.traceHint', { correlationId: event.correlationId })}>
                  <Link to={tracePath(searchParams, event.correlationId)} style={linkStyle}>
                    {t('logs.links.trace')}
                  </Link>
                </Tooltip>
              )}
            </Space>
          ) : (
            EMPTY
          ),
      },
    ];
  }, [searchParams, sortOrder, t, token.colorLink]);

  // A range the API rejects (its limit is 92 days) is said on the range control; everything else the user can act on
  // above the table. Network errors and 5xx are reported globally.
  const fieldErrors = isError && isApiError(error) ? error.problem?.errors : undefined;
  const rangeErrors = [...(fieldErrors?.from ?? []), ...(fieldErrors?.to ?? [])];
  const otherErrors = isError ? problemMessages(t, error).filter((message) => !rangeErrors.includes(message)) : [];
  const noRows = isError || idInvalid;

  return (
    <>
      <PageHeader
        title={t('logs.title')}
        extra={
          <Button
            icon={<ReloadOutlined />}
            loading={isFetching}
            disabled={windowMissing || idInvalid}
            onClick={() => void refetch()}
            style={{ width: SECONDARY_ACTION_WIDTH }}
          >
            {t('common.refresh')}
          </Button>
        }
      />
      <Space wrap style={{ marginBottom: rangeErrors.length > 0 ? 8 : 16 }}>
        <Segmented<RangePreset>
          block
          // Its own radio group (see LanguageSwitch).
          name="logs-range"
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
          placeholder={[t('logs.filters.from'), t('logs.filters.to')]}
          // Picked days are a custom range; a cleared picker is the default again.
          onChange={(picked) =>
            setRange({ from: picked?.[0]?.format(API_DATE_FORMAT), to: picked?.[1]?.format(API_DATE_FORMAT) })
          }
          style={{ width: DATE_RANGE_WIDTH }}
        />
      </Space>
      {rangeErrors.length > 0 && (
        <div role="alert" data-testid="logs-range-error" style={{ marginBottom: 16 }}>
          <Typography.Text type="danger">{rangeErrors.join(' ')}</Typography.Text>
        </div>
      )}
      <Space wrap align="start" style={{ marginBottom: 16 }}>
        <Select<EventLevel[]>
          mode="multiple"
          allowClear
          showSearch={false}
          maxTagCount={MAX_FILTER_TAGS}
          value={levels}
          placeholder={t('logs.filters.level')}
          aria-label={t('logs.filters.level')}
          options={EVENT_LEVELS.map((value) => ({ value, label: t(`logs.levels.${value}`) }))}
          // Written in the order of the API's enum, whatever the order of choosing: the same choice, the same address.
          onChange={(values) => setFilter({ [LEVEL_PARAM]: someOf(EVENT_LEVELS, values) })}
          style={{ width: MULTI_FILTER_WIDTH }}
        />
        <Select<string[]>
          mode="multiple"
          allowClear
          showSearch={false}
          // A short list: every option is in the document (and reachable by the keyboard) without virtual scrolling.
          virtual={false}
          maxTagCount={MAX_FILTER_TAGS}
          value={eventTypes}
          placeholder={t('logs.filters.eventType')}
          aria-label={t('logs.filters.eventType')}
          options={eventTypeOptions.map((value) => ({ value, label: eventTypeLabel(t, value) }))}
          onChange={(values) => setFilter({ [EVENT_TYPE_PARAM]: someOf(eventTypeOptions, values) })}
          style={{ width: MULTI_FILTER_WIDTH }}
        />
        <Input
          allowClear
          value={correlationText}
          placeholder={t('logs.filters.correlationId')}
          aria-label={t('logs.filters.correlationId')}
          onChange={(event) => setCorrelationText(event.target.value)}
          style={{ width: ID_FILTER_WIDTH }}
        />
        <IdFilter
          label={t('logs.filters.orderId')}
          text={orderText}
          invalid={order.invalid}
          testId="logs-order-id-error"
          onChange={setOrderText}
        />
        <IdFilter
          label={t('logs.filters.taskId')}
          text={taskText}
          invalid={task.invalid}
          testId="logs-task-id-error"
          onChange={setTaskText}
        />
        <Button
          onClick={() =>
            setFilter(Object.fromEntries([...FILTER_PARAMS, ...RANGE_PARAMS].map((name) => [name, undefined])))
          }
          style={{ width: SECONDARY_ACTION_WIDTH }}
        >
          {t('logs.filters.clear')}
        </Button>
      </Space>
      {otherErrors.length > 0 && (
        <Alert type="error" showIcon title={otherErrors.join(' ')} style={{ marginBottom: 16 }} />
      )}
      <Table<SystemEvent>
        rowKey="logId"
        size="small"
        columns={columns}
        dataSource={noRows ? [] : data?.items}
        loading={isFetching}
        tableLayout="fixed"
        scroll={{ x: COLUMNS_WIDTH }}
        // Starts descending (the default order), so the next click asks for ascending.
        sortDirections={['descend', 'ascend']}
        onChange={list.onTableChange}
        pagination={list.pagination(noRows ? 0 : data?.totalCount)}
      />
    </>
  );
}

interface IdFilterProps {
  label: string;
  text: string;
  /** The text in the address is no id: said under the field; nothing was asked with it. */
  invalid: boolean;
  testId: string;
  onChange: (text: string) => void;
}

/** A text filter for one id (a GUID), with the message under it when the text is none. */
function IdFilter({ label, text, invalid, testId, onChange }: IdFilterProps) {
  const { t } = useTranslation();

  return (
    <div style={{ width: ID_FILTER_WIDTH }}>
      <Input
        allowClear
        value={text}
        status={invalid ? 'error' : undefined}
        placeholder={label}
        aria-label={label}
        onChange={(event) => onChange(event.target.value)}
      />
      {invalid && (
        <div role="alert" data-testid={testId}>
          <Typography.Text type="danger">{t('logs.filters.idInvalid')}</Typography.Text>
        </div>
      )}
    </div>
  );
}
