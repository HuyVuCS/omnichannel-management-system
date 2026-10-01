import { EditOutlined, PlusOutlined } from '@ant-design/icons';
import { Button, Input, Space, Table, Tooltip, type TableProps } from 'antd';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { PAGE_ACTION_WIDTH, SEARCH_WIDTH, SINGLE_ACTION_COLUMN_WIDTH } from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useListQueryState } from '@/shared/hooks/useListQueryState';
import { formatDateTime } from '@/shared/utils/format';
import { CustomerFormModal, type CustomerFormState } from './components/CustomerFormModal';
import { CUSTOMER_SORT_FIELDS, useListCustomersQuery, type Customer } from './customersApi';

/** Long values (names, addresses) stay on one line; the full text is the tooltip. */
const truncated = (text: string | null) => <TruncatedText text={text} />;

/** Every signed-in role may read, create and edit customers (the order-entry page needs it). */
export function CustomersPage() {
  const { t } = useTranslation();
  const list = useListQueryState({ sortFields: CUSTOMER_SORT_FIELDS, defaultSortField: 'name' });
  const { data, isFetching } = useListCustomersQuery({
    page: list.page,
    pageSize: list.pageSize,
    search: list.search,
    sortBy: list.sortBy,
    sortDir: list.sortDir,
  });
  const [formState, setFormState] = useState<CustomerFormState>(null);

  const { sortOrder } = list;
  const columns = useMemo<TableProps<Customer>['columns']>(
    () => [
      {
        key: 'name',
        dataIndex: 'name',
        title: t('customers.columns.name'),
        width: 240,
        ellipsis: { showTitle: false },
        sorter: true,
        sortOrder: sortOrder('name'),
        render: truncated,
      },
      {
        key: 'phone',
        dataIndex: 'phone',
        title: t('customers.columns.phone'),
        width: 160,
        ellipsis: { showTitle: false },
        render: truncated,
      },
      {
        key: 'email',
        dataIndex: 'email',
        title: t('customers.columns.email'),
        width: 240,
        ellipsis: { showTitle: false },
        render: truncated,
      },
      {
        key: 'address',
        dataIndex: 'address',
        title: t('customers.columns.address'),
        width: 300,
        ellipsis: { showTitle: false },
        render: truncated,
      },
      {
        key: 'createdAt',
        dataIndex: 'createdAt',
        title: t('common.createdAt'),
        width: 170,
        ellipsis: true,
        sorter: true,
        sortOrder: sortOrder('createdAt'),
        render: (createdAt: string) => formatDateTime(createdAt),
      },
      {
        key: 'actions',
        title: t('common.actions'),
        width: SINGLE_ACTION_COLUMN_WIDTH,
        ellipsis: true,
        render: (_: unknown, customer) => (
          <Tooltip title={t('common.edit')}>
            <Button
              type="text"
              icon={<EditOutlined />}
              aria-label={t('common.edit')}
              onClick={() => setFormState({ kind: 'edit', customer })}
            />
          </Tooltip>
        ),
      },
    ],
    [sortOrder, t],
  );

  return (
    <>
      <PageHeader
        title={t('customers.title')}
        extra={
          <Button
            type="primary"
            icon={<PlusOutlined />}
            onClick={() => setFormState({ kind: 'create' })}
            style={{ width: PAGE_ACTION_WIDTH }}
          >
            {t('customers.create')}
          </Button>
        }
      />
      <Space wrap style={{ marginBottom: 16 }}>
        <Input.Search
          allowClear
          value={list.searchText}
          placeholder={t('customers.searchPlaceholder')}
          aria-label={t('customers.searchPlaceholder')}
          onChange={(event) => list.setSearchText(event.target.value)}
          style={{ width: SEARCH_WIDTH }}
        />
      </Space>
      <Table<Customer>
        rowKey="customerId"
        columns={columns}
        dataSource={data?.items}
        loading={isFetching}
        tableLayout="fixed"
        scroll={{ x: 1110 + SINGLE_ACTION_COLUMN_WIDTH }}
        sortDirections={['ascend', 'descend']}
        onChange={list.onTableChange}
        pagination={list.pagination(data?.totalCount)}
      />
      <CustomerFormModal state={formState} onClose={() => setFormState(null)} />
    </>
  );
}
