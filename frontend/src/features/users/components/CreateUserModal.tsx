import { Form, Input, Modal } from 'antd';
import { useTranslation } from 'react-i18next';
import { FieldHint } from '@/shared/components/FieldHint';
import { FormErrors } from '@/shared/components/FormErrors';
import { MODAL_WIDTH } from '@/shared/components/layout';
import { ResetFormOnOpen } from '@/shared/components/ResetFormOnOpen';
import { useFormSubmission } from '@/shared/hooks/useFormSubmission';
import { useOpenCount } from '@/shared/hooks/useOpenCount';
import { useCreateUserMutation, type CreateUserRequest } from '../usersApi';
import { RoleSelect } from './RoleSelect';

interface CreateUserModalProps {
  open: boolean;
  onClose: () => void;
}

const CODE_FIELDS = { user_email_exists: 'email' };

/**
 * Create-user form. Server errors are shown where they belong: validation errors (including the password policy) on
 * their fields in the API's localised wording, `user_email_exists` on the e-mail field, anything else above the form.
 */
export function CreateUserModal({ open, onClose }: CreateUserModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<CreateUserRequest>();
  const openCount = useOpenCount(open);
  const [createUser, { isLoading }] = useCreateUserMutation();
  const { submit, formErrors, clearErrors } = useFormSubmission(form, {
    action: (values) => createUser(values).unwrap(),
    successKey: 'users.messages.created',
    onSuccess: onClose,
    codeFields: CODE_FIELDS,
  });

  return (
    <Modal
      open={open}
      title={t('users.createModal.title')}
      width={MODAL_WIDTH}
      okText={t('common.create')}
      cancelText={t('common.cancel')}
      closable={{ 'aria-label': t('common.close') }}
      confirmLoading={isLoading}
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
        onFinish={(values) => void submit(values)}
      >
        <Form.Item name="email" label={t('users.createModal.email')} rules={[{ required: true }, { type: 'email' }]}>
          <Input autoComplete="off" />
        </Form.Item>
        <Form.Item
          name="password"
          label={t('users.createModal.password')}
          extra={<FieldHint text={t('users.createModal.passwordHint')} rows={2} />}
          rules={[{ required: true }]}
        >
          <Input.Password autoComplete="new-password" />
        </Form.Item>
        <Form.Item name="role" label={t('users.createModal.role')} rules={[{ required: true }]}>
          <RoleSelect />
        </Form.Item>
        <ResetFormOnOpen openCount={openCount} />
      </Form>
    </Modal>
  );
}
