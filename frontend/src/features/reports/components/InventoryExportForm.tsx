import { FileExcelOutlined } from '@ant-design/icons';
import { App as AntApp, Button, Form, Input, Space, Switch } from 'antd';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { FormErrors } from '@/shared/components/FormErrors';
import { FILTER_WIDTH, PAGE_ACTION_WIDTH, SEARCH_WIDTH } from '@/shared/components/layout';
import { applyExportProblem, browserTimeZone } from '../exportRequest';
import type { ExportPrefill } from '../paths';
import { useRequestInventoryExportMutation } from '../reportsApi';
import { ExportFileNote } from './ExportFileNote';

type InventoryPrefill = Extract<ExportPrefill, { reportType: 'Inventory' }>;

interface InventoryExportValues {
  search?: string;
  lowStockOnly?: boolean;
}

/** What the inventory list handed over, each value checked again (location state is not typed). */
function initialValues(prefill: InventoryPrefill | undefined): InventoryExportValues {
  return {
    search: typeof prefill?.search === 'string' ? prefill.search : undefined,
    lowStockOnly: prefill?.lowStockOnly === true,
  };
}

/**
 * Request of an inventory export: the two filters of the inventory list with the same meaning ("low stock" is the
 * server's rule, here as there). Time zone, language, limits and what happens after the 202: see `OrdersExportForm`.
 */
export function InventoryExportForm({ prefill }: { prefill?: InventoryPrefill }) {
  const { t } = useTranslation();
  const { message } = AntApp.useApp();
  const [form] = Form.useForm<InventoryExportValues>();
  const [requestExport, request] = useRequestInventoryExportMutation();
  const [formErrors, setFormErrors] = useState<string[]>([]);
  const [initial] = useState(() => initialValues(prefill));
  const timeZone = browserTimeZone();

  const submit = async (values: InventoryExportValues) => {
    setFormErrors([]);
    // No field has a rule of its own, so the form does not remove an API error of an earlier request.
    form.setFields([{ name: 'search', errors: [] }]);
    try {
      await requestExport({
        search: values.search?.trim() || undefined,
        lowStockOnly: values.lowStockOnly === true,
        timeZone,
      }).unwrap();
      message.success(t('reports.request.accepted'));
    } catch (error) {
      setFormErrors(applyExportProblem(form, error, t));
    }
  };

  return (
    <>
      <FormErrors errors={formErrors} />
      <Form
        form={form}
        // A name of its own: the ids of its fields must differ from those of the other form on the page.
        name="inventory-export"
        layout="vertical"
        initialValues={initial}
        onFinish={(values) => void submit(values)}
      >
        <Space align="start" size={16} wrap>
          <Form.Item name="search" label={t('reports.form.productSearch')} style={{ width: SEARCH_WIDTH }}>
            <Input allowClear autoComplete="off" placeholder={t('inventory.searchPlaceholder')} />
          </Form.Item>
          <Form.Item
            name="lowStockOnly"
            label={t('inventory.lowStockOnly')}
            valuePropName="checked"
            style={{ width: FILTER_WIDTH }}
          >
            <Switch />
          </Form.Item>
        </Space>
        <ExportFileNote timeZone={timeZone} />
        <Button
          type="primary"
          htmlType="submit"
          icon={<FileExcelOutlined />}
          loading={request.isLoading}
          disabled={request.isLoading}
          style={{ width: PAGE_ACTION_WIDTH }}
        >
          {t('reports.request.submit')}
        </Button>
      </Form>
    </>
  );
}
