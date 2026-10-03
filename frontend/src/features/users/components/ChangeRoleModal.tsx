import { Form, Modal, Typography } from 'antd';
import { useTranslation } from 'react-i18next';
import type { Role } from '@/features/auth/session';
import { FieldHint } from '@/shared/components/FieldHint';
import { FormErrors } from '@/shared/components/FormErrors';
import { MODAL_WIDTH } from '@/shared/components/layout';
import { ResetFormOnOpen } from '@/shared/components/ResetFormOnOpen';
import { useFormSubmission } from '@/shared/hooks/useFormSubmission';
import { useOpenCount } from '@/shared/hooks/useOpenCount';
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
  const [form] = Form.useForm<{ role: Role }>();
  const openCount = useOpenCount(user);
  const [changeRole, { isLoading }] = useChangeUserRoleMutation();
  const { submit, formErrors, clearErrors } = useFormSubmission(form, {
    action: ({ role }) => changeRole({ userId: user?.userId ?? '', role }).unwrap(),
    successKey: 'users.messages.roleChanged',
    onSuccess: onClose,
  });

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
        // With `ResetFormOnOpen`: a dialog opened again must not start with the values of the previous time.
        clearOnDestroy
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
        <ResetFormOnOpen openCount={openCount} />
      </Form>
    </Modal>
  );
}
