import { ReloadOutlined } from '@ant-design/icons';
import { Alert, Button, DatePicker, Input, Select, Space, Table, Tag, theme, Tooltip, type TableProps } from 'antd';
import dayjs from 'dayjs';
import { useCallback, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { orderPath } from '@/features/orders/paths';
import { ProductSelect } from '@/features/products/components/ProductSelect';
import { problemMessages } from '@/shared/api/problemDetails';
import {
  DATE_RANGE_WIDTH,
  FILTER_WIDTH,
  PRODUCT_SELECT_WIDTH,
  SECONDARY_ACTION_WIDTH,
} from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useAddressText } from '@/shared/hooks/useAddressText';
import { useListQueryState } from '@/shared/hooks/useListQueryState';
import { oneOf } from '@/shared/utils/address';
import { readLocalDayRange } from '@/shared/utils/dateRange';
import { API_DATE_FORMAT, DATE_FORMAT, formatDateTime, formatNumber } from '@/shared/utils/format';
import { isGuid } from '@/shared/utils/guid';
import {
  MOVEMENT_TYPES,
  REFERENCE_TYPES,
  useListMovementsQuery,
  type InventoryMovement,
  type MovementType,
  type ReferenceType,
} from './inventoryApi';
import { movementsPath } from './paths';

/** Filters of this page in the address (next to `page` / `pageSize`). */
const FILTER_PARAMS = ['productId', 'type', 'referenceType', 'referenceId', 'from', 'to'] as const;

const SYSTEM_ACTOR_PREFIX = 'system:';

/** One status colour per movement type. */
const TYPE_COLORS: Record<MovementType, string> = {
  Import: 'success',
  Reserve: 'processing',
  Release: 'default',
  Deduct: 'error',
  Adjustment: 'warning',
};

/**
 * History of every stock change, always newest first (the API does not sort this list on request). Filters live in the
 * address; values there that are not valid (an unknown type, a bad date, a product id that is no GUID) are ignored and
 * never sent. The date range is a range of local days (see `dateRange.ts`): the request carries the local start of
 * `from` and the local end of `to` with the UTC offset.
 */
export function MovementsPage() {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  const list = useListQueryState();
  const { searchParams, patchParams } = list;

  const rawProductId = searchParams.get('productId');
  const productId = isGuid(rawProductId) ? rawProductId : undefined;
  const type = oneOf(MOVEMENT_TYPES, searchParams.get('type'));
  const referenceType = oneOf(REFERENCE_TYPES, searchParams.get('referenceType'));
  const referenceId = searchParams.get('referenceId')?.trim() || undefined;
  const { from, to, fromInstant, toInstant } = readLocalDayRange(searchParams);

  const { data, error, isError, isFetching, refetch } = useListMovementsQuery(
    {
      page: list.page,
      pageSize: list.pageSize,
      productId,
      type,
      referenceType,
      referenceId,
      from: fromInstant,
      to: toInstant,
    },
    { refetchOnMountOrArgChange: true },
  );

  /** A changed filter starts on the first page. */
  const setFilter = useCallback(
    (changes: Partial<Record<(typeof FILTER_PARAMS)[number], string | undefined>>) =>
      patchParams({ ...changes, page: undefined }),
    [patchParams],
  );

  const commitReference = useCallback((text: string) => setFilter({ referenceId: text }), [setFilter]);
  const [referenceText, setReferenceText] = useAddressText(referenceId ?? '', commitReference);

  const columns = useMemo<TableProps<InventoryMovement>['columns']>(
    () => [
      {
        key: 'createdAt',
        dataIndex: 'createdAt',
        title: t('movements.columns.createdAt'),
        width: 170,
        ellipsis: true,
        render: (createdAt: string) => formatDateTime(createdAt),
      },
      {
        key: 'sku',
        dataIndex: 'sku',
        title: t('movements.columns.sku'),
        width: 160,
        ellipsis: { showTitle: false },
        render: (sku: string) => <TruncatedText text={sku} />,
      },
      {
        key: 'productName',
        dataIndex: 'productName',
        title: t('movements.columns.productName'),
        width: 240,
        ellipsis: { showTitle: false },
        render: (name: string) => <TruncatedText text={name} />,
      },
      {
        key: 'type',
        dataIndex: 'type',
        title: t('movements.columns.type'),
        width: 140,
        ellipsis: true,
        render: (movementType: MovementType) => (
          <Tag color={TYPE_COLORS[movementType]}>{t(`movements.types.${movementType}`, movementType)}</Tag>
        ),
      },
      {
        key: 'stock',
        dataIndex: 'stock',
        title: t('movements.columns.stock'),
        width: 110,
        align: 'right',
        ellipsis: true,
        render: (stock: number) => formatNumber(stock),
      },
      {
        key: 'reference',
        title: t('movements.columns.reference'),
        width: 280,
        ellipsis: { showTitle: false },
        render: (_: unknown, movement) => {
          const kind = t(`movements.referenceTypes.${movement.referenceType}`, movement.referenceType);
          const text = movement.referenceId ? `${kind}: ${movement.referenceId}` : kind;
          // A goods-receipt document links to all of its movements, an order to its detail page (order ids are GUIDs;
          // any other value stays text).
          const target =
            movement.referenceType === 'StockImport' && movement.referenceId
              ? movementsPath({ referenceType: 'StockImport', referenceId: movement.referenceId })
              : movement.referenceType === 'Order' && isGuid(movement.referenceId)
                ? orderPath(movement.referenceId)
                : undefined;
          if (!target) {
            return <TruncatedText text={text} />;
          }

          return (
            <Tooltip title={text}>
              <Link
                to={target}
                style={{
                  display: 'block',
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                  color: token.colorLink,
                }}
              >
                {text}
              </Link>
            </Tooltip>
          );
        },
      },
      {
        key: 'actor',
        title: t('movements.columns.actor'),
        width: 240,
        ellipsis: { showTitle: false },
        render: (_: unknown, movement) => {
          if (movement.createdByEmail) {
            return <TruncatedText text={movement.createdByEmail} />;
          }

          // Background processing (e.g. the Order Worker): named "System", the exact actor is the tooltip.
          if (movement.createdBy?.startsWith(SYSTEM_ACTOR_PREFIX)) {
            return (
              <Tooltip title={movement.createdBy}>
                <span>{t('movements.actor.system')}</span>
              </Tooltip>
            );
          }

          return <TruncatedText text={movement.createdBy} />;
        },
      },
    ],
    [t, token.colorLink],
  );

  // What the user can fix (a filter the API rejects) is shown above the table instead of an empty list; network errors
  // and 5xx are reported globally.
  const errorMessages = isError ? problemMessages(t, error) : [];

  return (
    <>
      <PageHeader
        title={t('movements.title')}
        extra={
          <Button
            icon={<ReloadOutlined />}
            loading={isFetching}
            onClick={() => void refetch()}
            style={{ width: SECONDARY_ACTION_WIDTH }}
          >
            {t('common.refresh')}
          </Button>
        }
      />
      <Space wrap style={{ marginBottom: 16 }}>
        <ProductSelect
          allowClear
          value={productId}
          placeholder={t('movements.filters.product')}
          aria-label={t('movements.filters.product')}
          onChange={(value) => setFilter({ productId: value })}
          style={{ width: PRODUCT_SELECT_WIDTH }}
        />
        <Select<MovementType>
          allowClear
          value={type}
          placeholder={t('movements.filters.type')}
          aria-label={t('movements.filters.type')}
          options={MOVEMENT_TYPES.map((value) => ({ value, label: t(`movements.types.${value}`) }))}
          onChange={(value) => setFilter({ type: value })}
          style={{ width: FILTER_WIDTH }}
        />
        <Select<ReferenceType>
          allowClear
          value={referenceType}
          placeholder={t('movements.filters.referenceType')}
          aria-label={t('movements.filters.referenceType')}
          options={REFERENCE_TYPES.map((value) => ({ value, label: t(`movements.referenceTypes.${value}`) }))}
          onChange={(value) => setFilter({ referenceType: value })}
          style={{ width: FILTER_WIDTH }}
        />
        <Input
          allowClear
          value={referenceText}
          placeholder={t('movements.filters.referenceId')}
          aria-label={t('movements.filters.referenceId')}
          onChange={(event) => setReferenceText(event.target.value)}
          style={{ width: FILTER_WIDTH }}
        />
        <DatePicker.RangePicker
          allowEmpty={[true, true]}
          format={DATE_FORMAT}
          value={[from ? dayjs(from) : null, to ? dayjs(to) : null]}
          placeholder={[t('movements.filters.from'), t('movements.filters.to')]}
          onChange={(range) =>
            setFilter({ from: range?.[0]?.format(API_DATE_FORMAT), to: range?.[1]?.format(API_DATE_FORMAT) })
          }
          style={{ width: DATE_RANGE_WIDTH }}
        />
        <Button
          onClick={() => setFilter(Object.fromEntries(FILTER_PARAMS.map((name) => [name, undefined])))}
          style={{ width: SECONDARY_ACTION_WIDTH }}
        >
          {t('movements.filters.clear')}
        </Button>
      </Space>
      {errorMessages.length > 0 && (
        <Alert type="error" showIcon title={errorMessages.join(' ')} style={{ marginBottom: 16 }} />
      )}
      <Table<InventoryMovement>
        rowKey="inventoryMovementId"
        columns={columns}
        dataSource={isError ? [] : data?.items}
        loading={isFetching}
        tableLayout="fixed"
        scroll={{ x: 1340 }}
        onChange={list.onTableChange}
        pagination={list.pagination(isError ? 0 : data?.totalCount)}
      />
    </>
  );
}
