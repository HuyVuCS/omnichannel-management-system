import { EditOutlined, ImportOutlined, PlusOutlined } from '@ant-design/icons';
import { Button, Input, Space, Table, Tooltip, type TableProps } from 'antd';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { useHasAnyRole } from '@/features/auth/useHasAnyRole';
import { importPath } from '@/features/inventory/paths';
import { DOUBLE_ACTION_COLUMN_WIDTH, PAGE_ACTION_WIDTH, SEARCH_WIDTH } from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useListQueryState } from '@/shared/hooks/useListQueryState';
import { formatDate, formatDateTime, formatMoney } from '@/shared/utils/format';
import { ProductFormModal, type ProductFormState } from './components/ProductFormModal';
import { StockStatusTag } from './components/StockStatusTag';
import { PRODUCT_SORT_FIELDS, useListProductsQuery, type Product } from './productsApi';

/**
 * The API lets Manager and Admin create and edit products, and import stock (the same roles, so the same check hides
 * the "import stock" action); everyone signed in may read products.
 */
const WRITE_ROLES = ['Admin', 'Manager'] as const;

export function ProductsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const canWrite = useHasAnyRole(WRITE_ROLES);
  const list = useListQueryState({ sortFields: PRODUCT_SORT_FIELDS, defaultSortField: 'sku' });
  const { data, isFetching } = useListProductsQuery({
    page: list.page,
    pageSize: list.pageSize,
    search: list.search,
    sortBy: list.sortBy,
    sortDir: list.sortDir,
  });
  const [formState, setFormState] = useState<ProductFormState>(null);

  const { sortOrder } = list;
  const columns = useMemo<TableProps<Product>['columns']>(
    () => [
      {
        key: 'sku',
        dataIndex: 'sku',
        title: t('products.columns.sku'),
        width: 180,
        ellipsis: { showTitle: false },
        sorter: true,
        sortOrder: sortOrder('sku'),
        render: (sku: string) => <TruncatedText text={sku} />,
      },
      {
        key: 'name',
        dataIndex: 'name',
        title: t('products.columns.name'),
        width: 280,
        ellipsis: { showTitle: false },
        sorter: true,
        sortOrder: sortOrder('name'),
        render: (name: string) => <TruncatedText text={name} />,
      },
      {
        key: 'price',
        dataIndex: 'price',
        title: t('products.columns.price'),
        width: 150,
        align: 'right',
        ellipsis: true,
        sorter: true,
        sortOrder: sortOrder('price'),
        render: (price: number) => formatMoney(price),
      },
      {
        key: 'availableStock',
        dataIndex: 'availableStock',
        title: t('products.columns.availableStock'),
        width: 140,
        align: 'right',
        ellipsis: true,
      },
      {
        key: 'stockStatus',
        title: t('products.columns.stockStatus'),
        width: 140,
        ellipsis: true,
        // The rule is the server's (`isLowStock`); it is only displayed here.
        render: (_: unknown, product) => <StockStatusTag isLowStock={product.isLowStock} />,
      },
      {
        key: 'expiryDate',
        dataIndex: 'expiryDate',
        title: t('products.columns.expiryDate'),
        width: 130,
        ellipsis: true,
        render: (expiryDate: string | null) => formatDate(expiryDate),
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
      // Hidden for roles that may not write; the API enforces the same rule.
      ...(canWrite
        ? [
            {
              key: 'actions',
              title: t('common.actions'),
              width: DOUBLE_ACTION_COLUMN_WIDTH,
              ellipsis: true,
              render: (_: unknown, product: Product) => (
                <Space size={4}>
                  <Tooltip title={t('common.edit')}>
                    <Button
                      type="text"
                      icon={<EditOutlined />}
                      aria-label={t('common.edit')}
                      onClick={() => setFormState({ kind: 'edit', product })}
                    />
                  </Tooltip>
                  <Tooltip title={t('products.actions.importStock')}>
                    <Button
                      type="text"
                      icon={<ImportOutlined />}
                      aria-label={t('products.actions.importStock')}
                      onClick={() => void navigate(importPath(product.productId))}
                    />
                  </Tooltip>
                </Space>
              ),
            },
          ]
        : []),
    ],
    [canWrite, navigate, sortOrder, t],
  );

  return (
    <>
      <PageHeader
        title={t('products.title')}
        extra={
          canWrite && (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              onClick={() => setFormState({ kind: 'create' })}
              style={{ width: PAGE_ACTION_WIDTH }}
            >
              {t('products.create')}
            </Button>
          )
        }
      />
      <Space wrap style={{ marginBottom: 16 }}>
        <Input.Search
          allowClear
          value={list.searchText}
          placeholder={t('products.searchPlaceholder')}
          aria-label={t('products.searchPlaceholder')}
          onChange={(event) => list.setSearchText(event.target.value)}
          style={{ width: SEARCH_WIDTH }}
        />
      </Space>
      <Table<Product>
        rowKey="productId"
        columns={columns}
        dataSource={data?.items}
        loading={isFetching}
        tableLayout="fixed"
        scroll={{ x: 1190 + (canWrite ? DOUBLE_ACTION_COLUMN_WIDTH : 0) }}
        sortDirections={['ascend', 'descend']}
        onChange={list.onTableChange}
        pagination={list.pagination(data?.totalCount)}
      />
      {canWrite && <ProductFormModal state={formState} onClose={() => setFormState(null)} />}
    </>
  );
}
