import { Form, Input, Modal } from 'antd';
import { useTranslation } from 'react-i18next';
import { FormErrors } from '@/shared/components/FormErrors';
import { MODAL_WIDTH } from '@/shared/components/layout';
import { ResetFormOnOpen } from '@/shared/components/ResetFormOnOpen';
import { useFormSubmission } from '@/shared/hooks/useFormSubmission';
import { useOpenCount } from '@/shared/hooks/useOpenCount';
import { useCreateCustomerMutation, useUpdateCustomerMutation, type Customer } from '../customersApi';

/** What the dialog is open for; null = closed. */
export type CustomerFormState = { kind: 'create' } | { kind: 'edit'; customer: Customer } | null;

interface CustomerFormModalProps {
  state: CustomerFormState;
  onClose: () => void;
}

interface CustomerFormValues {
  name: string;
  phone?: string;
  email?: string;
  address?: string;
}

/** Empty optional fields are sent as null, not as empty text. */
function orNull(value: string | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed ? trimmed : null;
}

/**
 * Create or edit a customer. The API's rules (phone characters, e-mail format, lengths) come back on the fields in the
 * API's localised wording.
 */
export function CustomerFormModal({ state, onClose }: CustomerFormModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<CustomerFormValues>();
  const openCount = useOpenCount(state);
  const [createCustomer, creating] = useCreateCustomerMutation();
  const [updateCustomer, updating] = useUpdateCustomerMutation();
  const editing = state?.kind === 'edit' ? state.customer : null;

  const { submit, formErrors, clearErrors } = useFormSubmission(form, {
    action: (values) => {
      const changes = {
        name: values.name,
        phone: orNull(values.phone),
        email: orNull(values.email),
        address: orNull(values.address),
      };
      return editing
        ? updateCustomer({ customerId: editing.customerId, changes }).unwrap()
        : createCustomer(changes).unwrap();
    },
    successKey: editing ? 'customers.messages.updated' : 'customers.messages.created',
    onSuccess: onClose,
  });

  const initialValues: Partial<CustomerFormValues> = editing
    ? {
        name: editing.name,
        phone: editing.phone ?? undefined,
        email: editing.email ?? undefined,
        address: editing.address ?? undefined,
      }
    : {};

  return (
    <Modal
      open={state !== null}
      title={t(editing ? 'customers.editModal.title' : 'customers.createModal.title')}
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
        // With `ResetFormOnOpen`: a dialog opened again must not start with the values of the previous time.
        clearOnDestroy
        initialValues={initialValues}
        onFinish={(values) => {
          // A field with a rule is validated again on every submit, which removes its old API error. Phone and address
          // have no rule here (theirs are the API's): their errors of the previous attempt are removed by hand.
          form.setFields([
            { name: 'phone', errors: [] },
            { name: 'address', errors: [] },
          ]);
          void submit(values);
        }}
      >
        <Form.Item name="name" label={t('customers.form.name')} rules={[{ required: true, whitespace: true }]}>
          <Input autoComplete="off" />
        </Form.Item>
        <Form.Item name="phone" label={t('customers.form.phone')}>
          <Input autoComplete="off" inputMode="tel" />
        </Form.Item>
        <Form.Item name="email" label={t('customers.form.email')} rules={[{ type: 'email' }]}>
          <Input autoComplete="off" />
        </Form.Item>
        <Form.Item name="address" label={t('customers.form.address')}>
          <Input.TextArea autoComplete="off" rows={2} style={{ resize: 'none' }} />
        </Form.Item>
        <ResetFormOnOpen openCount={openCount} />
      </Form>
    </Modal>
  );
}
