import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';
import { PageHeader } from '@/shared/components/PageHeader';
import { isGuid } from '@/shared/utils/guid';
import { StockImportForm, type ImportOutcome } from './components/StockImportForm';
import { StockImportResult } from './components/StockImportResult';

/**
 * Stock import (Manager/Admin): the form, and after a successful import the result with the stock the API returned.
 * `?productId=<id>` pre-fills the first line (links from the inventory and product lists).
 */
export function StockImportPage() {
  const { t } = useTranslation();
  const [searchParams, setSearchParams] = useSearchParams();
  const [outcome, setOutcome] = useState<ImportOutcome | null>(null);
  const productId = searchParams.get('productId');

  return (
    <>
      <PageHeader title={t('stockImport.title')} />
      {outcome ? (
        // A new form instance each time, so "new import" starts empty.
        <StockImportResult outcome={outcome} onNewImport={() => setOutcome(null)} />
      ) : (
        <StockImportForm
          initialProductId={isGuid(productId) ? productId : undefined}
          onImported={(imported) => {
            setOutcome(imported);
            // The pre-filled product belonged to the document that was just imported.
            setSearchParams({}, { replace: true });
          }}
        />
      )}
    </>
  );
}
