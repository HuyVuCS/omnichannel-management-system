import { ArrowLeftOutlined, EditOutlined, PlusOutlined } from '@ant-design/icons';
import { skipToken } from '@reduxjs/toolkit/query';
import {
  Alert,
  Button,
  Descriptions,
  Flex,
  Input,
  Result,
  Space,
  Table,
  Tag,
  theme,
  Tooltip,
  Typography,
  type TableProps,
} from 'antd';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useNavigate, useParams } from 'react-router';
import { useHasAnyRole } from '@/features/auth/useHasAnyRole';
import { processingListPath } from '@/features/processing/paths';
import { problemMessages } from '@/shared/api/problemDetails';
import { isApiError } from '@/shared/api/types';
import {
  PAGE_ACTION_WIDTH,
  SEARCH_WIDTH,
  SECONDARY_ACTION_WIDTH,
  SHORT_SUMMARY_LABEL_WIDTH,
  SINGLE_ACTION_COLUMN_WIDTH,
} from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useListQueryState } from '@/shared/hooks/useListQueryState';
import { formatDateTime } from '@/shared/utils/format';
import { isGuid } from '@/shared/utils/guid';
import { ChannelProductFormModal, type ChannelProductFormState } from './components/ChannelProductFormModal';
import { StockSyncTag } from './components/StockSyncTag';
import { SyncResultAlert } from './components/SyncResultAlert';
import {
  CHANNEL_PRODUCT_SORT_FIELDS,
  useGetChannelQuery,
  useListChannelProductsQuery,
  type ChannelProduct,
  type ChannelProductStatus,
  type SavedChannelProduct,
} from './channelsApi';
import { CHANNELS_PATH, MAPPING_WRITE_ROLES } from './paths';

/** What a mapping shows where it has no value (an external id that is not set, a mapping that was never changed). */
const EMPTY = '—';

/**
 * Sum of the column widths of the mapping table without the action column. With it the table is as wide as the
 * content area of the smallest supported window (1280 px wide: 992 px) minus a vertical scrollbar: no horizontal
 * scrolling there.
 */
const COLUMNS_WIDTH = 888;

/**
 * One channel (every role): what it is, whether stock is pushed to it, and the products mapped on it - the central
 * product and how the channel identifies it. Manager and Admin map products and change mappings here; the API's
 * answer to a save says whether a stock sync was started, and is shown above the list until the next save or until it
 * is dismissed (leaving the page drops it).
 *
 * Nothing about stock sync is decided here: which channels are synced (`stockSyncEnabled`), whether a save started a
 * sync (`syncTaskId`), and the rule that a mapping needs an external id are all the API's.
 *
 * Search, sort and paging of the mapping list live in the address. Mappings are changed by users only, so no realtime
 * hint names them; the list is fetched again whenever the page is opened and after a save.
 *
 * An id in the address that is no GUID is not sent; it and a channel the API does not know end on the not-found view.
 */
export function ChannelDetailPage() {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  const navigate = useNavigate();
  const { channelId } = useParams();
  const validId = isGuid(channelId) ? channelId : undefined;
  const canWrite = useHasAnyRole(MAPPING_WRITE_ROLES);
  const { data: channel, error, isError } = useGetChannelQuery(validId ?? skipToken);

  const list = useListQueryState({ sortFields: CHANNEL_PRODUCT_SORT_FIELDS, defaultSortField: 'sku' });
  const mappings = useListChannelProductsQuery(
    // Asked for once the channel is known: a channel that does not exist has no mapping list either.
    channel
      ? {
          channelId: channel.channelId,
          page: list.page,
          pageSize: list.pageSize,
          search: list.search,
          sortBy: list.sortBy,
          sortDir: list.sortDir,
        }
      : skipToken,
    { refetchOnMountOrArgChange: true },
  );

  const [formState, setFormState] = useState<ChannelProductFormState>(null);
  const [saved, setSaved] = useState<SavedChannelProduct | null>(null);

  const { sortOrder } = list;
  const columns = useMemo<TableProps<ChannelProduct>['columns']>(
    () => [
      {
        key: 'sku',
        dataIndex: 'sku',
        title: t('channelDetail.mappings.columns.sku'),
        width: 130,
        ellipsis: { showTitle: false },
        sorter: true,
        sortOrder: sortOrder('sku'),
        render: (sku: string) => <TruncatedText text={sku} />,
      },
      {
        key: 'productName',
        dataIndex: 'productName',
        title: t('channelDetail.mappings.columns.productName'),
        width: 150,
        ellipsis: { showTitle: false },
        render: (name: string) => <TruncatedText text={name} />,
      },
      {
        key: 'externalProductId',
        dataIndex: 'externalProductId',
        title: t('channelDetail.mappings.columns.externalProductId'),
        width: 124,
        ellipsis: { showTitle: false },
        render: (externalProductId: string | null) => <TruncatedText text={externalProductId ?? EMPTY} />,
      },
      {
        key: 'externalSku',
        dataIndex: 'externalSku',
        title: t('channelDetail.mappings.columns.externalSku'),
        width: 124,
        ellipsis: { showTitle: false },
        render: (externalSku: string | null) => <TruncatedText text={externalSku ?? EMPTY} />,
      },
      {
        key: 'status',
        dataIndex: 'status',
        title: t('channelDetail.mappings.columns.status'),
        width: 96,
        ellipsis: true,
        render: (status: ChannelProductStatus) => (
          <Tag color={status === 'Active' ? 'success' : 'default'} style={{ marginInlineEnd: 0 }}>
            {t(`channelDetail.mappingStatus.${status}`, status)}
          </Tag>
        ),
      },
      {
        key: 'createdAt',
        dataIndex: 'createdAt',
        title: t('channelDetail.mappings.columns.createdAt'),
        width: 132,
        ellipsis: true,
        sorter: true,
        sortOrder: sortOrder('createdAt'),
        render: (createdAt: string) => formatDateTime(createdAt),
      },
      {
        key: 'updatedAt',
        dataIndex: 'updatedAt',
        title: t('channelDetail.mappings.columns.updatedAt'),
        width: 132,
        ellipsis: true,
        render: (updatedAt: string | null) => formatDateTime(updatedAt) || EMPTY,
      },
      // Hidden for roles that may not write; the API enforces the same rule.
      ...(canWrite
        ? [
            {
              key: 'actions',
              title: t('common.actions'),
              width: SINGLE_ACTION_COLUMN_WIDTH,
              ellipsis: true,
              render: (_: unknown, mapping: ChannelProduct) => (
                <Tooltip title={t('common.edit')}>
                  <Button
                    type="text"
                    size="small"
                    icon={<EditOutlined />}
                    aria-label={t('common.edit')}
                    onClick={() => setFormState({ kind: 'edit', mapping })}
                  />
                </Tooltip>
              ),
            },
          ]
        : []),
    ],
    [canWrite, sortOrder, t],
  );

  const notFound = validId === undefined || (isError && isApiError(error) && error.status === 404);
  if (notFound) {
    return (
      <Result
        status="404"
        title={t('channelDetail.notFound.title')}
        subTitle={t('channelDetail.notFound.description')}
        extra={
          <Button type="primary" onClick={() => void navigate(CHANNELS_PATH)} style={{ width: PAGE_ACTION_WIDTH }}>
            {t('channelDetail.notFound.toList')}
          </Button>
        }
      />
    );
  }

  // What the user can act on (e.g. no permission, a list request the API rejects); network errors and 5xx are
  // reported globally.
  const errorMessages = isError ? problemMessages(t, error) : [];
  const listErrorMessages = mappings.isError ? problemMessages(t, mappings.error) : [];

  return (
    <>
      <PageHeader
        title={channel ? channel.name : t('channelDetail.loadingTitle')}
        extra={
          <Space>
            <Button
              icon={<ArrowLeftOutlined />}
              onClick={() => void navigate(CHANNELS_PATH)}
              style={{ width: SECONDARY_ACTION_WIDTH }}
            >
              {t('channelDetail.back')}
            </Button>
            {canWrite && channel && (
              <Button
                type="primary"
                icon={<PlusOutlined />}
                onClick={() => setFormState({ kind: 'create' })}
                style={{ width: PAGE_ACTION_WIDTH }}
              >
                {t('channelDetail.mappings.create')}
              </Button>
            )}
          </Space>
        }
      />
      {errorMessages.length > 0 && (
        <Alert type="error" showIcon title={errorMessages.join(' ')} style={{ marginBottom: 16 }} />
      )}
      {channel && (
        <>
          <Flex vertical gap={16} style={{ marginBottom: 24 }}>
            <Descriptions
              bordered
              size="small"
              column={2}
              styles={{ label: { width: SHORT_SUMMARY_LABEL_WIDTH } }}
              data-testid="channel-summary"
              items={[
                { key: 'name', label: t('channelDetail.summary.name'), children: channel.name },
                { key: 'slug', label: t('channelDetail.summary.slug'), children: channel.slug },
                {
                  key: 'type',
                  label: t('channelDetail.summary.type'),
                  children: t(`channels.types.${channel.type}`, channel.type),
                },
                {
                  key: 'status',
                  label: t('channelDetail.summary.status'),
                  children: (
                    <Tag color={channel.status === 'Active' ? 'success' : 'default'} style={{ marginInlineEnd: 0 }}>
                      {t(`channels.status.${channel.status}`, channel.status)}
                    </Tag>
                  ),
                },
                {
                  key: 'stockSync',
                  label: t('channelDetail.summary.stockSync'),
                  span: 2,
                  children: (
                    <Space size={16}>
                      <StockSyncTag enabled={channel.stockSyncEnabled} />
                      {channel.stockSyncEnabled && (
                        <Link
                          to={processingListPath({ taskType: 'StockSync', channelId: channel.channelId })}
                          style={{ color: token.colorLink }}
                        >
                          {t('channelDetail.syncTasks')}
                        </Link>
                      )}
                    </Space>
                  ),
                },
              ]}
            />
            {!channel.stockSyncEnabled && <Alert type="info" showIcon title={t('channels.stockSync.disabledHint')} />}
            {channel.status === 'Inactive' && <Alert type="info" showIcon title={t('channelDetail.inactiveHint')} />}
          </Flex>

          <Flex align="center" justify="space-between" gap={16} style={{ marginBottom: 16 }}>
            <Typography.Title level={5} style={{ margin: 0 }}>
              {t('channelDetail.mappings.title')}
            </Typography.Title>
            <Input.Search
              allowClear
              value={list.searchText}
              placeholder={t('channelDetail.mappings.searchPlaceholder')}
              aria-label={t('channelDetail.mappings.searchPlaceholder')}
              onChange={(event) => list.setSearchText(event.target.value)}
              style={{ width: SEARCH_WIDTH }}
            />
          </Flex>
          {saved && <SyncResultAlert saved={saved} onDismiss={() => setSaved(null)} />}
          {listErrorMessages.length > 0 && (
            <Alert type="error" showIcon title={listErrorMessages.join(' ')} style={{ marginBottom: 16 }} />
          )}
          <Table<ChannelProduct>
            rowKey="channelProductId"
            size="small"
            columns={columns}
            dataSource={mappings.isError ? [] : mappings.data?.items}
            loading={mappings.isFetching}
            tableLayout="fixed"
            scroll={{ x: COLUMNS_WIDTH + (canWrite ? SINGLE_ACTION_COLUMN_WIDTH : 0) }}
            sortDirections={['ascend', 'descend']}
            onChange={list.onTableChange}
            pagination={list.pagination(mappings.isError ? 0 : mappings.data?.totalCount)}
          />
          {canWrite && (
            <ChannelProductFormModal
              channel={channel}
              state={formState}
              onClose={() => setFormState(null)}
              onSaved={setSaved}
            />
          )}
        </>
      )}
    </>
  );
}
