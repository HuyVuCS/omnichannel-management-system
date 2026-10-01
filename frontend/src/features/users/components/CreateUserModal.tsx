import { Alert, App as AntApp, Form, Input, Modal } from 'antd';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { applyProblemToForm } from '@/shared/api/problemDetails';
import { FieldHint } from '@/shared/components/FieldHint';
import { MODAL_WIDTH } from '@/shared/components/layout';
import { useCreateUserMutation, type CreateUserRequest } from '../usersApi';
import { RoleSelect } from './RoleSelect';

interface CreateUserModalProps {
  open: boolean;
  onClose: () => void;
}

/**
 * Create-user form. Server errors are shown where they belong: validation errors (including the password policy) on
 * their fields in the API's localised wording, `user_email_exists` on the e-mail field, anything else above the form.
 */
export function CreateUserModal({ open, onClose }: CreateUserModalProps) {
  const { t } = useTranslation();
  const { message } = AntApp.useApp();
  const [form] = Form.useForm<CreateUserRequest>();
  const [createUser, { isLoading }] = useCreateUserMutation();
  const [formErrors, setFormErrors] = useState<string[]>([]);

  const submit = async (values: CreateUserRequest) => {
    setFormErrors([]);
    try {
      await createUser(values).unwrap();
      message.success(t('users.messages.created'));
      onClose();
    } catch (error) {
      setFormErrors(applyProblemToForm(form, error, t, { codeFields: { user_email_exists: 'email' } }));
    }
  };

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
      onCancel={onClose}
      afterClose={() => setFormErrors([])}
      destroyOnHidden
    >
      {formErrors.length > 0 && (
        <Alert type="error" showIcon message={formErrors.join(' ')} style={{ marginBottom: 16 }} />
      )}
      <Form form={form} layout="vertical" preserve={false} onFinish={(values) => void submit(values)}>
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
      </Form>
    </Modal>
  );
}
