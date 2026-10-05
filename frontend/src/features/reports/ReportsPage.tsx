import { Card, Segmented } from 'antd';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocation } from 'react-router';
import { PageHeader } from '@/shared/components/PageHeader';
import { ExportsTable } from './components/ExportsTable';
import { InventoryExportForm } from './components/InventoryExportForm';
import { OrdersExportForm } from './components/OrdersExportForm';
import { readExportPrefill } from './paths';
import { REPORT_TYPES, type ReportType } from './reportsApi';

/** Both forms have room for the longer of the two labels in either language. */
const TYPE_SWITCH_WIDTH = 320;

/**
 * Reports & Excel export (Manager and Admin; architecture doc §3.2): ask for an export of the orders or of the
 * inventory, and get the files of one's own exports - nothing else. There are no report views here: the dashboard is
 * the overview.
 *
 * Every export is built in the background (it is a processing task like any other), so a request only adds a row to
 * "My exports"; the row is followed there and the file downloaded from it. Opened from the "Export to Excel" button
 * of the orders or the inventory list, the matching form starts with that list's filters (`paths.ts`); nothing is
 * requested before the user submits.
 */
export function ReportsPage() {
  const { t } = useTranslation();
  const location = useLocation();
  // Read once: the forms take their start values from it, and a later change of the location (the table writes its
  // paging to the address) must not move them.
  const [prefill] = useState(() => readExportPrefill(location.state));
  const [reportType, setReportType] = useState<ReportType>(prefill?.reportType ?? 'Orders');

  return (
    <>
      <PageHeader title={t('reports.title')} />
      <Card title={t('reports.request.title')} style={{ marginBottom: 24 }}>
        <Segmented<ReportType>
          block
          // Its own radio group (see LanguageSwitch).
          name="reports-type"
          aria-label={t('reports.request.type')}
          value={reportType}
          options={REPORT_TYPES.map((value) => ({ value, label: t(`reports.types.${value}`) }))}
          onChange={setReportType}
          style={{ width: TYPE_SWITCH_WIDTH, marginBottom: 16 }}
        />
        {/* Both forms stay mounted: what was entered in one is still there after a look at the other. */}
        <div style={{ display: reportType === 'Orders' ? undefined : 'none' }} data-testid="orders-export-form">
          <OrdersExportForm prefill={prefill?.reportType === 'Orders' ? prefill : undefined} />
        </div>
        <div style={{ display: reportType === 'Inventory' ? undefined : 'none' }} data-testid="inventory-export-form">
          <InventoryExportForm prefill={prefill?.reportType === 'Inventory' ? prefill : undefined} />
        </div>
      </Card>
      <ExportsTable />
    </>
  );
}
