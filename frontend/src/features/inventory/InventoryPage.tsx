import { FileExcelOutlined, HistoryOutlined, ImportOutlined, ReloadOutlined } from '@ant-design/icons';
import { Button, Input, Space, Switch, Table, Tooltip, Typography, type TableProps } from 'antd';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { useHasAnyRole } from '@/features/auth/useHasAnyRole';
import { StockStatusTag } from '@/features/products/components/StockStatusTag';
import { exportPrefillState, REPORTS_PATH, REPORTS_ROLES } from '@/features/reports/paths';
import {
  DOUBLE_ACTION_COLUMN_WIDTH,
  FILTER_WIDTH,
  PAGE_ACTION_WIDTH,
  SEARCH_WIDTH,
  SECONDARY_ACTION_WIDTH,
} from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useListQueryState } from '@/shared/hooks/useListQueryState';
import { formatDateTime, formatNumber } from '@/shared/utils/format';
import { INVENTORY_SORT_FIELDS, useListInventoryQuery, type InventoryItem } from './inventoryApi';
import { importPath, LOW_STOCK_PARAM, movementsPath, STOCK_IMPORT_ROLES } from './paths';

/**
 * Stock per product. Every number and the low-stock flag come from the API as they are; nothing is computed here.
 * Stock also changes in the background (the Order Worker reserves it), so the list is fetched again whenever the page
 * is opened and on "Refresh"; realtime updates come with SignalR in a later task.
 */
export function InventoryPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const canImport = useHasAnyRole(STOCK_IMPORT_ROLES);
  const canExport = useHasAnyRole(REPORTS_ROLES);
  const list = useListQueryState({ sortFields: INVENTORY_SORT_FIELDS, defaultSortField: 'sku' });
  const lowStockOnly = list.searchParams.get(LOW_STOCK_PARAM) === 'true';
  const { data, isFetching, refetch } = useListInventoryQuery(
    {
      page: list.page,
      pageSize: list.pageSize,
      search: list.search,
      sortBy: list.sortBy,
      sortDir: list.sortDir,
      lowStockOnly: lowStockOnly || undefined,
    },
    { refetchOnMountOrArgChange: true },
  );

  const { sortOrder } = list;
  const columns = useMemo<TableProps<InventoryItem>['columns']>(() => {
    const quantity = (value: number) => formatNumber(value);

    return [
      {
        key: 'sku',
        dataIndex: 'sku',
        title: t('inventory.columns.sku'),
        width: 180,
        ellipsis: { showTitle: false },
        sorter: true,
        sortOrder: sortOrder('sku'),
        render: (sku: string) => <TruncatedText text={sku} />,
      },
      {
        key: 'name',
        dataIndex: 'name',
        title: t('inventory.columns.name'),
        width: 260,
        ellipsis: { showTitle: false },
        sorter: true,
        sortOrder: sortOrder('name'),
        render: (name: string) => <TruncatedText text={name} />,
      },
      {
        key: 'totalStock',
        dataIndex: 'totalStock',
        title: t('inventory.columns.totalStock'),
        width: 120,
        align: 'right',
        ellipsis: true,
        render: quantity,
      },
      {
        key: 'reservedStock',
        dataIndex: 'reservedStock',
        title: t('inventory.columns.reservedStock'),
        width: 120,
        align: 'right',
        ellipsis: true,
        render: quantity,
      },
      {
        key: 'availableStock',
        dataIndex: 'availableStock',
        title: t('inventory.columns.availableStock'),
        width: 140,
        align: 'right',
        ellipsis: true,
        sorter: true,
        sortOrder: sortOrder('availableStock'),
        render: quantity,
      },
      {
        key: 'lowStockThreshold',
        dataIndex: 'lowStockThreshold',
        title: t('inventory.columns.lowStockThreshold'),
        width: 170,
        align: 'right',
        ellipsis: true,
        render: quantity,
      },
      {
        key: 'stockStatus',
        title: t('inventory.columns.stockStatus'),
        width: 140,
        ellipsis: true,
        render: (_: unknown, item) => <StockStatusTag isLowStock={item.isLowStock} />,
      },
      {
        key: 'updatedAt',
        dataIndex: 'updatedAt',
        title: t('inventory.columns.updatedAt'),
        width: 170,
        ellipsis: true,
        sorter: true,
        sortOrder: sortOrder('updatedAt'),
        render: (updatedAt: string) => formatDateTime(updatedAt),
      },
      {
        key: 'actions',
        title: t('common.actions'),
        width: DOUBLE_ACTION_COLUMN_WIDTH,
        ellipsis: true,
        render: (_: unknown, item) => (
          <Space size={4}>
            <Tooltip title={t('inventory.actions.movements')}>
              <Button
                type="text"
                icon={<HistoryOutlined />}
                aria-label={t('inventory.actions.movements')}
                onClick={() => void navigate(movementsPath({ productId: item.productId }))}
              />
            </Tooltip>
            {/* Hidden for roles that may not import; the API enforces the same rule. */}
            {canImport && (
              <Tooltip title={t('inventory.actions.import')}>
                <Button
                  type="text"
                  icon={<ImportOutlined />}
                  aria-label={t('inventory.actions.import')}
                  onClick={() => void navigate(importPath(item.productId))}
                />
              </Tooltip>
            )}
          </Space>
        ),
      },
    ];
  }, [canImport, navigate, sortOrder, t]);

  return (
    <>
      <PageHeader
        title={t('inventory.title')}
        extra={
          <Space>
            <Button
              icon={<ReloadOutlined />}
              loading={isFetching}
              onClick={() => void refetch()}
              style={{ width: SECONDARY_ACTION_WIDTH }}
            >
              {t('common.refresh')}
            </Button>
            {/* Hidden for roles that may not export; the API enforces the same rule. */}
            {canExport && (
              <Button
                icon={<FileExcelOutlined />}
                // To the Reports page with the filters of this list: the form there is filled in, and nothing is
                // requested before the user submits it.
                onClick={() =>
                  void navigate(REPORTS_PATH, {
                    state: exportPrefillState({ reportType: 'Inventory', search: list.search, lowStockOnly }),
                  })
                }
                style={{ width: PAGE_ACTION_WIDTH }}
              >
                {t('reports.exportToExcel')}
              </Button>
            )}
            {canImport && (
              <Button
                type="primary"
                icon={<ImportOutlined />}
                onClick={() => void navigate(importPath())}
                style={{ width: PAGE_ACTION_WIDTH }}
              >
                {t('inventory.actions.import')}
              </Button>
            )}
          </Space>
        }
      />
      <Space wrap style={{ marginBottom: 16 }}>
        <Input.Search
          allowClear
          value={list.searchText}
          placeholder={t('inventory.searchPlaceholder')}
          aria-label={t('inventory.searchPlaceholder')}
          onChange={(event) => list.setSearchText(event.target.value)}
          style={{ width: SEARCH_WIDTH }}
        />
        <label style={{ width: FILTER_WIDTH, display: 'flex', alignItems: 'center', gap: 8 }}>
          <Switch
            checked={lowStockOnly}
            // A changed filter starts on the first page.
            onChange={(checked) =>
              list.patchParams({ [LOW_STOCK_PARAM]: checked ? 'true' : undefined, page: undefined })
            }
          />
          <Typography.Text ellipsis={{ tooltip: t('inventory.lowStockOnly') }} style={{ minWidth: 0 }}>
            {t('inventory.lowStockOnly')}
          </Typography.Text>
        </label>
      </Space>
      <Table<InventoryItem>
        rowKey="productId"
        columns={columns}
        dataSource={data?.items}
        loading={isFetching}
        tableLayout="fixed"
        scroll={{ x: 1300 + DOUBLE_ACTION_COLUMN_WIDTH }}
        sortDirections={['ascend', 'descend']}
        onChange={list.onTableChange}
        pagination={list.pagination(data?.totalCount)}
      />
    </>
  );
}
