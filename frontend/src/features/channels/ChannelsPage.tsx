import { EditOutlined, PlusOutlined } from '@ant-design/icons';
import { Button, Table, Tag, Tooltip, type TableProps } from 'antd';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useHasAnyRole } from '@/features/auth/useHasAnyRole';
import { PAGE_ACTION_WIDTH, SINGLE_ACTION_COLUMN_WIDTH } from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { ChannelFormModal, type ChannelFormState } from './components/ChannelFormModal';
import { useListChannelsQuery, type Channel, type ChannelStatus, type ChannelType } from './channelsApi';

/** Only an Admin may create or change channels; everyone signed in may see them. */
const WRITE_ROLES = ['Admin'] as const;

export function ChannelsPage() {
  const { t } = useTranslation();
  const canWrite = useHasAnyRole(WRITE_ROLES);
  const { data, isFetching } = useListChannelsQuery();
  const [formState, setFormState] = useState<ChannelFormState>(null);

  const columns = useMemo<TableProps<Channel>['columns']>(
    () => [
      {
        key: 'name',
        dataIndex: 'name',
        title: t('channels.columns.name'),
        width: 280,
        ellipsis: { showTitle: false },
        render: (name: string) => <TruncatedText text={name} />,
      },
      {
        key: 'slug',
        dataIndex: 'slug',
        title: t('channels.columns.slug'),
        width: 240,
        ellipsis: { showTitle: false },
        render: (slug: string) => <TruncatedText text={slug} />,
      },
      {
        key: 'type',
        dataIndex: 'type',
        title: t('channels.columns.type'),
        width: 220,
        ellipsis: true,
        render: (type: ChannelType) => <Tag>{t(`channels.types.${type}`)}</Tag>,
      },
      {
        key: 'status',
        dataIndex: 'status',
        title: t('channels.columns.status'),
        width: 180,
        ellipsis: true,
        render: (status: ChannelStatus) => (
          <Tag color={status === 'Active' ? 'success' : 'default'}>{t(`channels.status.${status}`)}</Tag>
        ),
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
    [canWrite, t],
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
        scroll={{ x: 920 + (canWrite ? SINGLE_ACTION_COLUMN_WIDTH : 0) }}
        pagination={false}
      />
      {canWrite && <ChannelFormModal state={formState} onClose={() => setFormState(null)} />}
    </>
  );
}
