import { EditOutlined, PlusOutlined } from '@ant-design/icons';
import { Button, Table, Tag, theme, Tooltip, type TableProps } from 'antd';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { useHasAnyRole } from '@/features/auth/useHasAnyRole';
import { PAGE_ACTION_WIDTH, SINGLE_ACTION_COLUMN_WIDTH } from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { ChannelFormModal, type ChannelFormState } from './components/ChannelFormModal';
import { StockSyncTag } from './components/StockSyncTag';
import { useListChannelsQuery, type Channel, type ChannelStatus, type ChannelType } from './channelsApi';
import { channelPath } from './paths';

/** Only an Admin may create or change channels; everyone signed in may see them. */
const WRITE_ROLES = ['Admin'] as const;

/**
 * Sum of the column widths without the action column. With it the table is as wide as the content area of the
 * smallest supported window (1280 px wide: 992 px) minus a vertical scrollbar: no horizontal scrolling there.
 */
const COLUMNS_WIDTH = 888;

/**
 * The channels (every role): name, slug, type, status and whether stock is pushed to the channel at all. The name
 * opens the channel's page with the products mapped on it.
 */
export function ChannelsPage() {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  const canWrite = useHasAnyRole(WRITE_ROLES);
  const { data, isFetching } = useListChannelsQuery();
  const [formState, setFormState] = useState<ChannelFormState>(null);

  const columns = useMemo<TableProps<Channel>['columns']>(
    () => [
      {
        key: 'name',
        dataIndex: 'name',
        title: t('channels.columns.name'),
        width: 220,
        ellipsis: { showTitle: false },
        render: (name: string, channel) => (
          <Tooltip title={name}>
            <Link
              to={channelPath(channel.channelId)}
              style={{
                display: 'block',
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
                color: token.colorLink,
              }}
            >
              {name}
            </Link>
          </Tooltip>
        ),
      },
      {
        key: 'slug',
        dataIndex: 'slug',
        title: t('channels.columns.slug'),
        width: 168,
        ellipsis: { showTitle: false },
        render: (slug: string) => <TruncatedText text={slug} />,
      },
      {
        key: 'type',
        dataIndex: 'type',
        title: t('channels.columns.type'),
        width: 200,
        ellipsis: true,
        render: (type: ChannelType) => <Tag>{t(`channels.types.${type}`)}</Tag>,
      },
      {
        key: 'status',
        dataIndex: 'status',
        title: t('channels.columns.status'),
        width: 150,
        ellipsis: true,
        render: (status: ChannelStatus) => (
          <Tag color={status === 'Active' ? 'success' : 'default'}>{t(`channels.status.${status}`)}</Tag>
        ),
      },
      {
        key: 'stockSyncEnabled',
        dataIndex: 'stockSyncEnabled',
        title: t('channels.columns.stockSync'),
        width: 150,
        ellipsis: { showTitle: false },
        // The rule is the server's (`stockSyncEnabled`); it is only displayed here.
        render: (enabled: boolean) => <StockSyncTag enabled={enabled} />,
      },
      // Hidden for roles that may not write; the API enforces the same rule.
      ...(canWrite
        ? [
            {
              key: 'actions',
              title: t('common.actions'),
              width: SINGLE_ACTION_COLUMN_WIDTH,
              ellipsis: true,
              render: (_: unknown, channel: Channel) => (
                <Tooltip title={t('common.edit')}>
                  <Button
                    type="text"
                    icon={<EditOutlined />}
                    aria-label={t('common.edit')}
                    onClick={() => setFormState({ kind: 'edit', channel })}
                  />
                </Tooltip>
              ),
            },
          ]
        : []),
    ],
    [canWrite, t, token.colorLink],
  );

  return (
    <>
      <PageHeader
        title={t('channels.title')}
        extra={
          canWrite && (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              onClick={() => setFormState({ kind: 'create' })}
              style={{ width: PAGE_ACTION_WIDTH }}
            >
              {t('channels.create')}
            </Button>
          )
        }
      />
      <Table<Channel>
        rowKey="channelId"
        columns={columns}
        dataSource={data}
        loading={isFetching}
        tableLayout="fixed"
        scroll={{ x: COLUMNS_WIDTH + (canWrite ? SINGLE_ACTION_COLUMN_WIDTH : 0) }}
        pagination={false}
      />
      {canWrite && <ChannelFormModal state={formState} onClose={() => setFormState(null)} />}
    </>
  );
}
