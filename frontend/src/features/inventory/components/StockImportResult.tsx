import { Button, Result, Table, type TableProps } from 'antd';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { PAGE_ACTION_WIDTH, WIDE_ACTION_WIDTH } from '@/shared/components/layout';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { formatNumber } from '@/shared/utils/format';
import { movementsPath } from '../paths';
import type { ImportedLine, ImportOutcome } from './StockImportForm';

interface StockImportResultProps {
  outcome: ImportOutcome;
  /** Start another document with an empty form. */
  onNewImport: () => void;
}

/** What an applied import did: the document reference and, per line, the stock the API reported right after it. */
export function StockImportResult({ outcome, onNewImport }: StockImportResultProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();

  const columns = useMemo<TableProps<ImportedLine>['columns']>(() => {
    const quantity = (value: number) => formatNumber(value);

    return [
      {
        key: 'product',
        dataIndex: 'product',
        title: t('stockImport.form.product'),
        width: 360,
        ellipsis: { showTitle: false },
        render: (product: string) => <TruncatedText text={product} />,
      },
      {
        key: 'stock',
        dataIndex: 'stock',
        title: t('stockImport.result.imported'),
        width: 140,
        align: 'right',
        ellipsis: true,
        render: quantity,
      },
      {
        key: 'totalStock',
        dataIndex: 'totalStock',
        title: t('inventory.columns.totalStock'),
        width: 140,
        align: 'right',
        ellipsis: true,
        render: quantity,
      },
      {
        key: 'reservedStock',
        dataIndex: 'reservedStock',
        title: t('inventory.columns.reservedStock'),
        width: 140,
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
        render: quantity,
      },
    ];
  }, [t]);

  return (
    <>
      <Result
        status="success"
        title={t('stockImport.result.title')}
        subTitle={t('stockImport.result.reference', { reference: outcome.referenceId })}
        extra={[
          // Fixed widths: the texts differ per language, the buttons must not move.
          <Button
            key="movements"
            type="primary"
            onClick={() =>
              void navigate(movementsPath({ referenceType: 'StockImport', referenceId: outcome.referenceId }))
            }
            style={{ width: WIDE_ACTION_WIDTH }}
          >
            {t('stockImport.result.viewMovements')}
          </Button>,
          <Button key="new" onClick={onNewImport} style={{ width: PAGE_ACTION_WIDTH }}>
            {t('stockImport.result.newImport')}
          </Button>,
        ]}
      />
      <Table<ImportedLine>
        rowKey="movementId"
        columns={columns}
        dataSource={outcome.lines}
        tableLayout="fixed"
        scroll={{ x: 920 }}
        pagination={false}
      />
    </>
  );
}
