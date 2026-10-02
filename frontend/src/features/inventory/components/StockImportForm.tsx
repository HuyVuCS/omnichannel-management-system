import { DeleteOutlined, ImportOutlined, PlusOutlined } from '@ant-design/icons';
import { skipToken } from '@reduxjs/toolkit/query';
import { Alert, Button, Descriptions, Form, Input, InputNumber, Modal, Space, Tooltip } from 'antd';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ProductSelect } from '@/features/products/components/ProductSelect';
import { productLabel, useGetProductQuery, type Product } from '@/features/products/productsApi';
import { applyProblemToForm } from '@/shared/api/problemDetails';
import { FormErrors } from '@/shared/components/FormErrors';
import { MODAL_WIDTH, PAGE_ACTION_WIDTH, PRODUCT_SELECT_WIDTH, QUANTITY_INPUT_WIDTH } from '@/shared/components/layout';
import { formatNumber } from '@/shared/utils/format';
import { useImportInventoryMutation, type ImportInventoryRequest } from '../inventoryApi';

interface ImportLine {
  productId?: string;
  stock?: number;
}

interface ImportFormValues {
  documentNumber: string;
  documentSeries?: string;
  items: ImportLine[];
}

/** One imported line as the result view shows it: what was sent plus the stock the API returned. */
export interface ImportedLine {
  movementId: string;
  product: string;
  stock: number;
  totalStock: number;
  reservedStock: number;
  availableStock: number;
}

export interface ImportOutcome {
  referenceId: string;
  lines: ImportedLine[];
}

interface StockImportFormProps {
  /** Product of the first line (from the address); the user can change it. */
  initialProductId?: string;
  onImported: (outcome: ImportOutcome) => void;
}

const DOCUMENT_SERIES_WIDTH = 240;

/**
 * Label area of fields that stand side by side: a fixed height with the label at its bottom. The label of a required
 * field is a little taller than that of an optional one (its asterisk), which would put the two inputs on different
 * lines.
 */
const SIDE_BY_SIDE_LABEL = { style: { display: 'flex', alignItems: 'flex-end', height: 32 } };

/** A document that was already applied belongs to the document number: that is what the user has to change. */
const CODE_FIELDS = { stock_import_already_applied: 'documentNumber' };

/**
 * A goods-receipt document: number, optional series and its lines (product + quantity). Submitting asks for
 * confirmation first, because an import cannot be undone (there is no reversal). The limits (lines per document,
 * quantity per line, the same product twice, a document applied twice) are the API's; its errors are shown on the
 * field, under the list or above the form.
 */
export function StockImportForm({ initialProductId, onImported }: StockImportFormProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<ImportFormValues>();
  const [importInventory, importing] = useImportInventoryMutation();
  const [confirming, setConfirming] = useState<ImportFormValues | null>(null);
  const [formErrors, setFormErrors] = useState<string[]>([]);
  const lines = Form.useWatch('items', form);

  // Names of the products on the lines, for the result view: the API answers with ids only. Same cache entry as the
  // picker's own lookup of the pre-filled product, so this is not a second request.
  const initialProduct = useGetProductQuery(initialProductId ?? skipToken).data;
  const pickedProducts = useRef(new Map<string, Product>());

  const submit = async (values: ImportFormValues) => {
    const request: ImportInventoryRequest = {
      documentNumber: values.documentNumber,
      documentSeries: values.documentSeries?.trim() ? values.documentSeries : null,
      items: values.items.map((line) => ({ productId: line.productId ?? '', stock: line.stock ?? 0 })),
    };

    try {
      const response = await importInventory(request).unwrap();
      const nameOf = (productId: string) => {
        const product =
          pickedProducts.current.get(productId) ??
          (initialProduct?.productId === productId ? initialProduct : undefined);
        return product ? productLabel(product) : productId;
      };

      onImported({
        referenceId: response.referenceId,
        lines: response.items.map((item, index) => ({
          movementId: item.movementId,
          product: nameOf(item.productId),
          stock: request.items[index]?.stock ?? 0,
          totalStock: item.totalStock,
          reservedStock: item.reservedStock,
          availableStock: item.availableStock,
        })),
      });
    } catch (error) {
      setConfirming(null);
      setFormErrors(applyProblemToForm(form, error, t, { codeFields: CODE_FIELDS }));
    }
  };

  const chosenIds = (lines ?? []).flatMap((line) => (line?.productId ? [line.productId] : []));

  return (
    <>
      <FormErrors errors={formErrors} />
      <Form
        form={form}
        layout="vertical"
        initialValues={{ items: [{ productId: initialProductId }] }}
        onFinish={(values) => {
          setFormErrors([]);
          // The fields were validated again just now, which removed their old API errors. The list has no rule of its
          // own, so an API error of the list as a whole (the same product twice) is removed here.
          form.setFields([{ name: 'items', errors: [] }]);
          setConfirming(values);
        }}
      >
        <Space align="start" size={16} wrap>
          <Form.Item
            name="documentNumber"
            label={t('stockImport.form.documentNumber')}
            labelCol={SIDE_BY_SIDE_LABEL}
            rules={[{ required: true, whitespace: true }]}
            style={{ width: PRODUCT_SELECT_WIDTH }}
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Form.Item
            name="documentSeries"
            label={t('stockImport.form.documentSeries')}
            labelCol={SIDE_BY_SIDE_LABEL}
            style={{ width: DOCUMENT_SERIES_WIDTH }}
          >
            <Input autoComplete="off" placeholder={t('common.optional')} />
          </Form.Item>
        </Space>

        <Form.List name="items">
          {(fields, { add, remove }, { errors }) => (
            <Form.Item label={t('stockImport.form.lines')} required>
              {fields.map((field, index) => (
                <Space key={field.key} align="start" size={8}>
                  <Form.Item
                    name={[field.name, 'productId']}
                    rules={[{ required: true }]}
                    messageVariables={{ label: t('stockImport.form.product') }}
                    style={{ width: PRODUCT_SELECT_WIDTH }}
                  >
                    <ProductSelect
                      aria-label={t('stockImport.form.productOfLine', { line: index + 1 })}
                      // Convenience only: the API rejects a product that is on two lines.
                      disabledIds={chosenIds}
                      onChange={(_productId, product) => {
                        if (product) {
                          pickedProducts.current.set(product.productId, product);
                        }
                      }}
                    />
                  </Form.Item>
                  <Form.Item
                    name={[field.name, 'stock']}
                    rules={[{ required: true }]}
                    messageVariables={{ label: t('stockImport.form.quantity') }}
                    style={{ width: QUANTITY_INPUT_WIDTH }}
                  >
                    <InputNumber<number>
                      min={1}
                      precision={0}
                      controls={false}
                      placeholder={t('stockImport.form.quantity')}
                      aria-label={t('stockImport.form.quantityOfLine', { line: index + 1 })}
                      style={{ width: '100%' }}
                    />
                  </Form.Item>
                  <Tooltip title={t('stockImport.form.removeLine')}>
                    <Button
                      type="text"
                      icon={<DeleteOutlined />}
                      aria-label={t('stockImport.form.removeLineOf', { line: index + 1 })}
                      disabled={fields.length === 1}
                      onClick={() => remove(field.name)}
                    />
                  </Tooltip>
                </Space>
              ))}
              <div>
                <Button
                  type="dashed"
                  icon={<PlusOutlined />}
                  onClick={() => add({})}
                  style={{ width: PRODUCT_SELECT_WIDTH }}
                >
                  {t('stockImport.form.addLine')}
                </Button>
              </div>
              {/* Errors of the list as a whole (the same product twice, too many lines). */}
              <Form.ErrorList errors={errors} />
            </Form.Item>
          )}
        </Form.List>

        <Button
          type="primary"
          htmlType="submit"
          icon={<ImportOutlined />}
          loading={importing.isLoading}
          style={{ width: PAGE_ACTION_WIDTH }}
        >
          {t('stockImport.form.submit')}
        </Button>
      </Form>

      <Modal
        open={confirming !== null}
        title={t('stockImport.confirm.title')}
        width={MODAL_WIDTH}
        okText={t('stockImport.confirm.ok')}
        cancelText={t('common.cancel')}
        closable={{ 'aria-label': t('common.close'), disabled: importing.isLoading }}
        mask={{ closable: !importing.isLoading }}
        keyboard={!importing.isLoading}
        confirmLoading={importing.isLoading}
        cancelButtonProps={{ disabled: importing.isLoading }}
        onOk={() => {
          if (confirming) {
            void submit(confirming);
          }
        }}
        onCancel={() => setConfirming(null)}
        destroyOnHidden
      >
        {confirming && (
          <>
            <Descriptions
              column={1}
              size="small"
              style={{ marginBottom: 16 }}
              items={[
                {
                  key: 'documentNumber',
                  label: t('stockImport.form.documentNumber'),
                  children: confirming.documentNumber,
                },
                ...(confirming.documentSeries?.trim()
                  ? [
                      {
                        key: 'documentSeries',
                        label: t('stockImport.form.documentSeries'),
                        children: confirming.documentSeries,
                      },
                    ]
                  : []),
                {
                  key: 'lineCount',
                  label: t('stockImport.confirm.lineCount'),
                  children: formatNumber(confirming.items.length),
                },
                {
                  key: 'totalQuantity',
                  label: t('stockImport.confirm.totalQuantity'),
                  children: formatNumber(confirming.items.reduce((sum, line) => sum + (line.stock ?? 0), 0)),
                },
              ]}
            />
            <Alert type="warning" showIcon title={t('stockImport.confirm.warning')} />
          </>
        )}
      </Modal>
    </>
  );
}
