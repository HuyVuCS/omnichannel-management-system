import { DatePicker, Form, Input, InputNumber, Modal } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import { useTranslation } from 'react-i18next';
import { FieldHint } from '@/shared/components/FieldHint';
import { FormErrors } from '@/shared/components/FormErrors';
import { MODAL_WIDTH } from '@/shared/components/layout';
import { MoneyInput } from '@/shared/components/MoneyInput';
import { useFormSubmission } from '@/shared/hooks/useFormSubmission';
import { useResetFormOnOpen } from '@/shared/hooks/useResetFormOnOpen';
import { API_DATE_FORMAT, DATE_FORMAT } from '@/shared/utils/format';
import { useCreateProductMutation, useUpdateProductMutation, type Product } from '../productsApi';

/** What the dialog is open for; null = closed. */
export type ProductFormState = { kind: 'create' } | { kind: 'edit'; product: Product } | null;

interface ProductFormModalProps {
  state: ProductFormState;
  onClose: () => void;
}

interface ProductFormValues {
  sku: string;
  name: string;
  price: number;
  lowStockThreshold: number;
  expiryDate: Dayjs | null;
}

const CODE_FIELDS = { product_sku_exists: 'sku' };

/**
 * Create or edit a product. The SKU can only be set on creation (shown upper-cased while typing; the server
 * normalises and validates it). Stock is not edited here: it only changes through stock imports and orders.
 */
export function ProductFormModal({ state, onClose }: ProductFormModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<ProductFormValues>();
  useResetFormOnOpen(form, state);
  const [createProduct, creating] = useCreateProductMutation();
  const [updateProduct, updating] = useUpdateProductMutation();
  const editing = state?.kind === 'edit' ? state.product : null;

  const { submit, formErrors, clearErrors } = useFormSubmission(form, {
    action: ({ sku, expiryDate, ...rest }) => {
      const changes = { ...rest, expiryDate: expiryDate ? expiryDate.format(API_DATE_FORMAT) : null };
      return editing
        ? updateProduct({ productId: editing.productId, changes }).unwrap()
        : createProduct({ sku, ...changes }).unwrap();
    },
    successKey: editing ? 'products.messages.updated' : 'products.messages.created',
    onSuccess: onClose,
    codeFields: CODE_FIELDS,
  });

  const initialValues: Partial<ProductFormValues> = editing
    ? {
        sku: editing.sku,
        name: editing.name,
        price: editing.price,
        lowStockThreshold: editing.lowStockThreshold,
        expiryDate: editing.expiryDate ? dayjs(editing.expiryDate) : null,
      }
    : { lowStockThreshold: 0, expiryDate: null };

  return (
    <Modal
      open={state !== null}
      title={t(editing ? 'products.editModal.title' : 'products.createModal.title')}
      width={MODAL_WIDTH}
      okText={t(editing ? 'common.save' : 'common.create')}
      cancelText={t('common.cancel')}
      closable={{ 'aria-label': t('common.close') }}
      confirmLoading={creating.isLoading || updating.isLoading}
      onOk={() => form.submit()}
      onCancel={() => {
        // Cleared now, not after the close animation: the dialog may be reopened before that.
        clearErrors();
        onClose();
      }}
      destroyOnHidden
    >
      <FormErrors errors={formErrors} />
      <Form
        form={form}
        layout="vertical"
        preserve={false}
        // With `useResetFormOnOpen`: a dialog opened again must not start with the values of the previous time.
        clearOnDestroy
        initialValues={initialValues}
        onFinish={(values) => void submit(values)}
      >
        <Form.Item
          name="sku"
          label={t('products.form.sku')}
          extra={<FieldHint text={t('products.form.skuHint')} rows={2} />}
          rules={[{ required: true }]}
        >
          <Input autoComplete="off" disabled={editing !== null} style={{ textTransform: 'uppercase' }} />
        </Form.Item>
        <Form.Item name="name" label={t('products.form.name')} rules={[{ required: true }]}>
          <Input autoComplete="off" />
        </Form.Item>
        <Form.Item name="price" label={t('products.form.price')} rules={[{ required: true }]}>
          <MoneyInput />
        </Form.Item>
        <Form.Item
          name="lowStockThreshold"
          label={t('products.form.lowStockThreshold')}
          extra={<FieldHint text={t('products.form.lowStockThresholdHint')} />}
          rules={[{ required: true }]}
        >
          <InputNumber<number> min={0} precision={0} style={{ width: '100%' }} />
        </Form.Item>
        <Form.Item name="expiryDate" label={t('products.form.expiryDate')}>
          <DatePicker format={DATE_FORMAT} style={{ width: '100%' }} />
        </Form.Item>
      </Form>
    </Modal>
  );
}
