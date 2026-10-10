import { App as AntApp, Descriptions, Form, Input, Modal, Select } from 'antd';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { applyProblemToForm } from '@/shared/api/problemDetails';
import { FieldHint } from '@/shared/components/FieldHint';
import { FormErrors } from '@/shared/components/FormErrors';
import { MODAL_WIDTH } from '@/shared/components/layout';
import { ResetFormOnOpen } from '@/shared/components/ResetFormOnOpen';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useOpenCount } from '@/shared/hooks/useOpenCount';
import { formatMoney } from '@/shared/utils/format';
import { PAYMENT_METHODS, usePayOrderMutation, type OrderDetail, type PaymentMethod } from '../ordersApi';

interface PayOrderModalProps {
  /** The order that is paid (its number and total are shown, never editable). */
  order: OrderDetail;
  open: boolean;
  onClose: () => void;
}

interface PayOrderFormValues {
  paymentMethod: PaymentMethod;
  paymentReference?: string;
}

/**
 * The API's limit for a payment reference (`Order.MaxPaymentReferenceLength`); here only an input limit, so a longer
 * text cannot be typed. The server stays the authority.
 */
export const MAX_PAYMENT_REFERENCE_LENGTH = 100;

/**
 * "Mark as paid" (Manager/Admin): records the payment of an order. The dialog is the confirmation - it shows the
 * order number and the total, and the payment method has no default: a financial record is chosen, not accepted.
 *
 * The rules are the API's: an order that was paid meanwhile or cannot be paid (409) is said above the form in the UI
 * language, and the page behind the dialog is fetched again (`payOrder`); validation errors come back on their fields.
 * After a payment the page shows the new state the same way, and the invoice and e-mail tasks change live through
 * realtime hints.
 */
export function PayOrderModal({ order, open, onClose }: PayOrderModalProps) {
  const { t } = useTranslation();
  const { message } = AntApp.useApp();
  const [form] = Form.useForm<PayOrderFormValues>();
  const openCount = useOpenCount(open);
  const [payOrder, { isLoading }] = usePayOrderMutation();
  const [formErrors, setFormErrors] = useState<string[]>([]);

  const submit = async (payment: PayOrderFormValues) => {
    setFormErrors([]);
    try {
      const paid = await payOrder({ orderId: order.orderId, payment }).unwrap();
      message.success(
        t(paid.emailTaskId ? 'orderDetail.payModal.paidWithEmail' : 'orderDetail.payModal.paidWithoutEmail'),
      );
      onClose();
    } catch (error) {
      setFormErrors(applyProblemToForm(form, error, t));
    }
  };

  return (
    <Modal
      open={open}
      title={t('orderDetail.payModal.title')}
      width={MODAL_WIDTH}
      okText={t('orderDetail.payment.markPaid')}
      cancelText={t('common.cancel')}
      closable={{ 'aria-label': t('common.close') }}
      // Loading and disabled while the request runs: a second click sends nothing (the API's 409 is the guard anyway).
      confirmLoading={isLoading}
      okButtonProps={{ disabled: isLoading }}
      onOk={() => form.submit()}
      onCancel={() => {
        // Cleared now, not after the close animation: the dialog may be reopened before that.
        setFormErrors([]);
        onClose();
      }}
      destroyOnHidden
    >
      <FormErrors errors={formErrors} />
      <Descriptions
        size="small"
        column={1}
        style={{ marginBottom: 16 }}
        items={[
          {
            key: 'order',
            label: t('orderDetail.payModal.order'),
            children: <TruncatedText text={order.externalOrderId} />,
          },
          { key: 'total', label: t('orderDetail.payModal.total'), children: formatMoney(order.totalAmount) },
        ]}
      />
      <Form
        form={form}
        layout="vertical"
        preserve={false}
        // With `ResetFormOnOpen`: a dialog opened again must not start with the values of the previous time.
        clearOnDestroy
        onFinish={(values) => {
          if (isLoading) {
            return;
          }

          // A field with a rule is validated again on every submit, which removes its old API error. The reference
          // has no rule here (its limit is the input's): its error of the previous attempt is removed by hand.
          form.setFields([{ name: 'paymentReference', errors: [] }]);
          void submit(values);
        }}
      >
        <Form.Item name="paymentMethod" label={t('orderDetail.payModal.method')} rules={[{ required: true }]}>
          <Select<PaymentMethod>
            placeholder={t('orderDetail.payModal.methodPlaceholder')}
            options={PAYMENT_METHODS.map((value) => ({ value, label: t(`orders.paymentMethods.${value}`) }))}
          />
        </Form.Item>
        <Form.Item
          name="paymentReference"
          label={t('orderDetail.payModal.reference')}
          extra={<FieldHint text={t('orderDetail.payModal.referenceHint')} />}
        >
          <Input autoComplete="off" maxLength={MAX_PAYMENT_REFERENCE_LENGTH} />
        </Form.Item>
        <ResetFormOnOpen openCount={openCount} />
      </Form>
    </Modal>
  );
}
