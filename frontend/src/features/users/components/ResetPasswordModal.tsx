import { Form, Input, Modal, Typography } from 'antd';
import { useTranslation } from 'react-i18next';
import { FieldHint } from '@/shared/components/FieldHint';
import { FormErrors } from '@/shared/components/FormErrors';
import { MODAL_WIDTH } from '@/shared/components/layout';
import { useFormSubmission } from '@/shared/hooks/useFormSubmission';
import { useResetFormOnOpen } from '@/shared/hooks/useResetFormOnOpen';
import { useResetUserPasswordMutation, type User } from '../usersApi';

interface ResetPasswordModalProps {
  /** The user whose password is reset; null = closed. */
  user: User | null;
  onClose: () => void;
}

/** Sets a temporary password. Policy errors come back on `newPassword` in the API's localised wording. */
export function ResetPasswordModal({ user, onClose }: ResetPasswordModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<{ newPassword: string }>();
  useResetFormOnOpen(form, user);
  const [resetPassword, { isLoading }] = useResetUserPasswordMutation();
  const { submit, formErrors, clearErrors } = useFormSubmission(form, {
    action: ({ newPassword }) => resetPassword({ userId: user?.userId ?? '', newPassword }).unwrap(),
    successKey: 'users.messages.passwordReset',
    onSuccess: onClose,
  });

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
      onCancel={() => {
        // Cleared now, not after the close animation: the dialog may be reopened before that.
        clearErrors();
        onClose();
      }}
      destroyOnHidden
    >
      <FormErrors errors={formErrors} />
      <Typography.Paragraph ellipsis={{ tooltip: user?.email }} strong>
        {user?.email}
      </Typography.Paragraph>
      <Form
        form={form}
        layout="vertical"
        preserve={false}
        // With `useResetFormOnOpen`: a dialog opened again must not start with the values of the previous time.
        clearOnDestroy
        onFinish={(values) => void submit(values)}
      >
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
