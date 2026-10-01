import { DeleteOutlined, PlusOutlined, SyncOutlined } from '@ant-design/icons';
import { Button, Form, Input, InputNumber, Space, Tooltip } from 'antd';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { CustomerSelect } from '@/features/customers/components/CustomerSelect';
import { ProductSelect } from '@/features/products/components/ProductSelect';
import { applyProblemToForm } from '@/shared/api/problemDetails';
import { FormErrors } from '@/shared/components/FormErrors';
import { PAGE_ACTION_WIDTH, PRODUCT_SELECT_WIDTH, QUANTITY_INPUT_WIDTH } from '@/shared/components/layout';
import { generateExternalOrderId } from '../externalOrderId';
import { useSubmitOrderMutation, type SubmitOrderRequest } from '../ordersApi';

interface OrderLine {
  productId?: string;
  stock?: number;
}

interface OrderFormValues {
  externalOrderId: string;
  customerId?: string;
  items: OrderLine[];
}

/** An order the API accepted (202): what the outcome view needs to follow it. */
export interface AcceptedOrder {
  taskId: string;
  externalOrderId: string;
  /** The channel had already accepted this order; `taskId` is the task of the first submission. */
  duplicate: boolean;
  /** When the 202 arrived (epoch milliseconds): start of the time the outcome is polled for. */
  acceptedAt: number;
}

interface OrderEntryFormProps {
  /** Channel slug the order is sent to (`POST /api/orders/{slug}`). */
  slug: string;
  /** Prefix of generated external order ids (`DP`, `WEB`, `POS`). */
  idPrefix: string;
  onAccepted: (order: AcceptedOrder) => void;
}

/**
 * An external order id that was already used on this channel with other content belongs to the id field: a new number
 * is what the user has to choose.
 */
const CODE_FIELDS = { order_conflict: 'externalOrderId' };

/**
 * The order form of one channel panel: external order id, customer and lines (product + quantity). There is no price
 * and no total here - amounts come from the order the API creates - and no limit other than "a whole number from 1":
 * lines per order, quantity per line, the same product twice, an unknown product or customer and an inactive channel are
 * all the API's to decide; its errors are shown on the field, under the list or above the form.
 *
 * The external order id is the order's idempotency key. It is generated when the form opens and stays as it is after a
 * failed request, so sending again is the same order (after a lost response the API answers "already accepted" instead
 * of creating a second one). That is also why there is no confirmation: a double submit cannot create two orders.
 */
export function OrderEntryForm({ slug, idPrefix, onAccepted }: OrderEntryFormProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<OrderFormValues>();
  const [submitOrder, submitting] = useSubmitOrderMutation();
  const [formErrors, setFormErrors] = useState<string[]>([]);
  // Once per form: "New order" mounts a new form and with it a new number.
  const [initialExternalOrderId] = useState(() => generateExternalOrderId(idPrefix));
  const lines = Form.useWatch('items', form);

  const submit = async (values: OrderFormValues) => {
    setFormErrors([]);
    // The fields are validated again on every submit, which removes their old API errors. The list has no rule of its
    // own, so an API error of the list as a whole (the same product twice) is removed here.
    form.setFields([{ name: 'items', errors: [] }]);
    const order: SubmitOrderRequest = {
      externalOrderId: values.externalOrderId,
      customerId: values.customerId ?? '',
      items: values.items.map((line) => ({ productId: line.productId ?? '', stock: line.stock ?? 0 })),
    };

    try {
      const accepted = await submitOrder({ slug, order }).unwrap();
      onAccepted({
        taskId: accepted.taskId,
        externalOrderId: order.externalOrderId.trim(),
        duplicate: accepted.duplicate,
        acceptedAt: Date.now(),
      });
    } catch (error) {
      setFormErrors(applyProblemToForm(form, error, t, { codeFields: CODE_FIELDS }));
    }
  };

  const chosenIds = (lines ?? []).flatMap((line) => (line?.productId ? [line.productId] : []));

  return (
    <>
      <FormErrors errors={formErrors} />
      <Form
        form={form}
        // Three forms share the page: the name keeps the ids of their fields apart.
        name={`order-entry-${slug}`}
        layout="vertical"
        initialValues={{ externalOrderId: initialExternalOrderId, items: [{}] }}
        onFinish={(values) => void submit(values)}
      >
        <Space align="start" size={16} wrap>
          <Form.Item
            name="externalOrderId"
            label={t('orderEntry.form.externalOrderId')}
            rules={[{ required: true, whitespace: true }]}
            style={{ width: PRODUCT_SELECT_WIDTH }}
          >
            <Input
              autoComplete="off"
              suffix={
                <Tooltip title={t('orderEntry.form.newNumber')}>
                  <Button
                    type="text"
                    size="small"
                    icon={<SyncOutlined />}
                    aria-label={t('orderEntry.form.newNumber')}
                    onClick={() =>
                      form.setFields([
                        { name: 'externalOrderId', value: generateExternalOrderId(idPrefix), errors: [] },
                      ])
                    }
                  />
                </Tooltip>
              }
            />
          </Form.Item>
          <Form.Item
            name="customerId"
            label={t('orderEntry.form.customer')}
            rules={[{ required: true }]}
            style={{ width: PRODUCT_SELECT_WIDTH }}
          >
            <CustomerSelect />
          </Form.Item>
        </Space>

        <Form.List name="items">
          {(fields, { add, remove }, { errors }) => (
            <Form.Item label={t('orderEntry.form.lines')} required>
              {fields.map((field, index) => (
                <Space key={field.key} align="start" size={8}>
                  <Form.Item
                    name={[field.name, 'productId']}
                    rules={[{ required: true }]}
                    messageVariables={{ label: t('orderEntry.form.product') }}
                    style={{ width: PRODUCT_SELECT_WIDTH }}
                  >
                    <ProductSelect
                      aria-label={t('orderEntry.form.productOfLine', { line: index + 1 })}
                      // Convenience only: the API rejects a product that is on two lines.
                      disabledIds={chosenIds}
                    />
                  </Form.Item>
                  <Form.Item
                    name={[field.name, 'stock']}
                    rules={[{ required: true }]}
                    messageVariables={{ label: t('orderEntry.form.quantity') }}
                    style={{ width: QUANTITY_INPUT_WIDTH }}
                  >
                    <InputNumber<number>
                      min={1}
                      precision={0}
                      controls={false}
                      placeholder={t('orderEntry.form.quantity')}
                      aria-label={t('orderEntry.form.quantityOfLine', { line: index + 1 })}
                      style={{ width: '100%' }}
                    />
                  </Form.Item>
                  <Tooltip title={t('orderEntry.form.removeLine')}>
                    <Button
                      type="text"
                      icon={<DeleteOutlined />}
                      aria-label={t('orderEntry.form.removeLineOf', { line: index + 1 })}
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
                  {t('orderEntry.form.addLine')}
                </Button>
              </div>
              {/* Errors of the list as a whole (the same product twice, too many lines). */}
              <Form.ErrorList errors={errors} />
            </Form.Item>
          )}
        </Form.List>

        <Button type="primary" htmlType="submit" loading={submitting.isLoading} style={{ width: PAGE_ACTION_WIDTH }}>
          {t('orderEntry.form.submit')}
        </Button>
      </Form>
    </>
  );
}
