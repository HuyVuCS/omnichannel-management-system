import { EditOutlined, ImportOutlined, PlusOutlined } from '@ant-design/icons';
import { Button, Input, Space, Table, Tooltip, type TableProps } from 'antd';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { useHasAnyRole } from '@/features/auth/useHasAnyRole';
import { importPath } from '@/features/inventory/paths';
import { PAGE_ACTION_WIDTH, SEARCH_WIDTH, SINGLE_ACTION_COLUMN_WIDTH } from '@/shared/components/layout';
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

/**
 * Column widths. With the action column (two small icon buttons) they add up to the content area of the smallest
 * supported window (1280 px wide: 992 px) minus a vertical scrollbar: no horizontal scrolling there, in either
 * language. The table uses the medium size for it: the default cell padding would cost 16 px more per column.
 */
const WIDTHS = {
  sku: 124,
  name: 180,
  price: 118,
  availableStock: 112,
  stockStatus: 104,
  expiryDate: 104,
  createdAt: 144,
} as const;
const COLUMNS_WIDTH = Object.values(WIDTHS).reduce((sum, width) => sum + width, 0);

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
        width: WIDTHS.sku,
        ellipsis: { showTitle: false },
        sorter: true,
        sortOrder: sortOrder('sku'),
        render: (sku: string) => <TruncatedText text={sku} />,
      },
      {
        key: 'name',
        dataIndex: 'name',
        title: t('products.columns.name'),
        width: WIDTHS.name,
        ellipsis: { showTitle: false },
        sorter: true,
        sortOrder: sortOrder('name'),
        render: (name: string) => <TruncatedText text={name} />,
      },
      {
        key: 'price',
        dataIndex: 'price',
        title: t('products.columns.price'),
        width: WIDTHS.price,
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
        width: WIDTHS.availableStock,
        align: 'right',
        ellipsis: true,
      },
      {
        key: 'stockStatus',
        title: t('products.columns.stockStatus'),
        width: WIDTHS.stockStatus,
        ellipsis: true,
        // The rule is the server's (`isLowStock`); it is only displayed here.
        render: (_: unknown, product) => <StockStatusTag isLowStock={product.isLowStock} />,
      },
      {
        key: 'expiryDate',
        dataIndex: 'expiryDate',
        title: t('products.columns.expiryDate'),
        width: WIDTHS.expiryDate,
        ellipsis: true,
        render: (expiryDate: string | null) => formatDate(expiryDate),
      },
      {
        key: 'createdAt',
        dataIndex: 'createdAt',
        title: t('common.createdAt'),
        width: WIDTHS.createdAt,
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
              width: SINGLE_ACTION_COLUMN_WIDTH,
              ellipsis: true,
              render: (_: unknown, product: Product) => (
                <Space size={4}>
                  <Tooltip title={t('common.edit')}>
                    <Button
                      type="text"
                      size="small"
                      icon={<EditOutlined />}
                      aria-label={t('common.edit')}
                      onClick={() => setFormState({ kind: 'edit', product })}
                    />
                  </Tooltip>
                  <Tooltip title={t('products.actions.importStock')}>
                    <Button
                      type="text"
                      size="small"
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
        size="middle"
        columns={columns}
        dataSource={data?.items}
        loading={isFetching}
        tableLayout="fixed"
        scroll={{ x: COLUMNS_WIDTH + (canWrite ? SINGLE_ACTION_COLUMN_WIDTH : 0) }}
        sortDirections={['ascend', 'descend']}
        onChange={list.onTableChange}
        pagination={list.pagination(data?.totalCount)}
      />
      {canWrite && <ProductFormModal state={formState} onClose={() => setFormState(null)} />}
    </>
  );
}
