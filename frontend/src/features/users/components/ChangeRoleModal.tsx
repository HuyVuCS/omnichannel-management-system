import { Alert, App as AntApp, Form, Modal, Typography } from 'antd';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { Role } from '@/features/auth/session';
import { applyProblemToForm } from '@/shared/api/problemDetails';
import { FieldHint } from '@/shared/components/FieldHint';
import { MODAL_WIDTH } from '@/shared/components/layout';
import { useChangeUserRoleMutation, type User } from '../usersApi';
import { RoleSelect } from './RoleSelect';

interface ChangeRoleModalProps {
  /** The user to change; null = closed. */
  user: User | null;
  onClose: () => void;
}

/** Changes a user's role. `last_admin` and `cannot_modify_self` from the API are shown translated above the form. */
export function ChangeRoleModal({ user, onClose }: ChangeRoleModalProps) {
  const { t } = useTranslation();
  const { message } = AntApp.useApp();
  const [form] = Form.useForm<{ role: Role }>();
  const [changeRole, { isLoading }] = useChangeUserRoleMutation();
  const [formErrors, setFormErrors] = useState<string[]>([]);

  const submit = async ({ role }: { role: Role }) => {
    if (!user) {
      return;
    }

    setFormErrors([]);
    try {
      await changeRole({ userId: user.userId, role }).unwrap();
      message.success(t('users.messages.roleChanged'));
      onClose();
    } catch (error) {
      setFormErrors(applyProblemToForm(form, error, t));
    }
  };

  return (
    <Modal
      open={user !== null}
      title={t('users.roleModal.title')}
      width={MODAL_WIDTH}
      okText={t('common.save')}
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
      <Typography.Paragraph ellipsis={{ tooltip: user?.email }} strong>
        {user?.email}
      </Typography.Paragraph>
      <Form
        form={form}
        layout="vertical"
        preserve={false}
        initialValues={{ role: user?.role ?? undefined }}
        onFinish={(values) => void submit(values)}
      >
        <Form.Item
          name="role"
          label={t('users.roleModal.role')}
          extra={<FieldHint text={t('users.roleModal.hint')} rows={2} />}
          rules={[{ required: true }]}
        >
          <RoleSelect />
        </Form.Item>
      </Form>
    </Modal>
  );
}
