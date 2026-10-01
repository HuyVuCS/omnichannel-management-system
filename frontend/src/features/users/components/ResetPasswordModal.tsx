import { Alert, App as AntApp, Form, Input, Modal, Typography } from 'antd';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { applyProblemToForm } from '@/shared/api/problemDetails';
import { FieldHint } from '@/shared/components/FieldHint';
import { MODAL_WIDTH } from '@/shared/components/layout';
import { useResetUserPasswordMutation, type User } from '../usersApi';

interface ResetPasswordModalProps {
  /** The user whose password is reset; null = closed. */
  user: User | null;
  onClose: () => void;
}

/** Sets a temporary password. Policy errors come back on `newPassword` in the API's localised wording. */
export function ResetPasswordModal({ user, onClose }: ResetPasswordModalProps) {
  const { t } = useTranslation();
  const { message } = AntApp.useApp();
  const [form] = Form.useForm<{ newPassword: string }>();
  const [resetPassword, { isLoading }] = useResetUserPasswordMutation();
  const [formErrors, setFormErrors] = useState<string[]>([]);

  const submit = async ({ newPassword }: { newPassword: string }) => {
    if (!user) {
      return;
    }

    setFormErrors([]);
    try {
      await resetPassword({ userId: user.userId, newPassword }).unwrap();
      message.success(t('users.messages.passwordReset'));
      onClose();
    } catch (error) {
      setFormErrors(applyProblemToForm(form, error, t));
    }
  };

  return (
    <Modal
      open={user !== null}
      title={t('users.resetModal.title')}
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
      <Form form={form} layout="vertical" preserve={false} onFinish={(values) => void submit(values)}>
        <Form.Item
          name="newPassword"
          label={t('users.resetModal.newPassword')}
          extra={<FieldHint text={t('users.resetModal.hint')} rows={2} />}
          rules={[{ required: true }]}
        >
          <Input.Password autoComplete="new-password" />
        </Form.Item>
      </Form>
    </Modal>
  );
}
