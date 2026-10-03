import { App as AntApp, Form, Input, Modal, Select } from 'antd';
import { useTranslation } from 'react-i18next';
import { FieldHint } from '@/shared/components/FieldHint';
import { FormErrors } from '@/shared/components/FormErrors';
import { MODAL_WIDTH } from '@/shared/components/layout';
import { ResetFormOnOpen } from '@/shared/components/ResetFormOnOpen';
import { useFormSubmission } from '@/shared/hooks/useFormSubmission';
import { useOpenCount } from '@/shared/hooks/useOpenCount';
import {
  CHANNEL_STATUSES,
  CHANNEL_TYPES,
  useCreateChannelMutation,
  useUpdateChannelMutation,
  type Channel,
  type ChannelStatus,
  type ChannelType,
} from '../channelsApi';

/** What the dialog is open for; null = closed. */
export type ChannelFormState = { kind: 'create' } | { kind: 'edit'; channel: Channel } | null;

interface ChannelFormModalProps {
  state: ChannelFormState;
  onClose: () => void;
}

interface ChannelFormValues {
  name: string;
  slug: string;
  type: ChannelType;
  status: ChannelStatus;
}

const CODE_FIELDS = { channel_slug_exists: 'slug' };

/**
 * Create or edit a channel (Admin). Slug and type can only be set on creation: orders and webhooks are routed by the
 * slug. Deactivating a channel asks for confirmation first, because the API then rejects its orders.
 */
export function ChannelFormModal({ state, onClose }: ChannelFormModalProps) {
  const { t } = useTranslation();
  const { modal } = AntApp.useApp();
  const [form] = Form.useForm<ChannelFormValues>();
  const openCount = useOpenCount(state);
  const [createChannel, creating] = useCreateChannelMutation();
  const [updateChannel, updating] = useUpdateChannelMutation();
  const editing = state?.kind === 'edit' ? state.channel : null;

  const { submit, formErrors, clearErrors } = useFormSubmission(form, {
    action: ({ name, slug, type, status }) =>
      editing
        ? updateChannel({ channelId: editing.channelId, changes: { name, status } }).unwrap()
        : createChannel({ name, slug, type }).unwrap(),
    successKey: editing ? 'channels.messages.updated' : 'channels.messages.created',
    onSuccess: onClose,
    codeFields: CODE_FIELDS,
  });

  const onFinish = (values: ChannelFormValues) => {
    const deactivating = editing?.status === 'Active' && values.status === 'Inactive';
    if (!deactivating) {
      void submit(values);
      return;
    }

    modal.confirm({
      title: t('channels.confirm.deactivateTitle'),
      content: t('channels.confirm.deactivateContent', { name: editing.name }),
      okText: t('channels.confirm.deactivateOk'),
      cancelText: t('common.cancel'),
      okButtonProps: { danger: true },
      onOk: () => submit(values),
    });
  };

  const initialValues: Partial<ChannelFormValues> = editing
    ? { name: editing.name, slug: editing.slug, type: editing.type, status: editing.status }
    : { status: 'Active' };

  return (
    <Modal
      open={state !== null}
      title={t(editing ? 'channels.editModal.title' : 'channels.createModal.title')}
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
        onFinish={onFinish}
      >
        <Form.Item name="name" label={t('channels.form.name')} rules={[{ required: true, whitespace: true }]}>
          <Input autoComplete="off" />
        </Form.Item>
        <Form.Item
          name="slug"
          label={t('channels.form.slug')}
          extra={<FieldHint text={t('channels.form.slugHint')} rows={2} />}
          rules={[{ required: true }]}
        >
          <Input autoComplete="off" disabled={editing !== null} />
        </Form.Item>
        <Form.Item name="type" label={t('channels.form.type')} rules={[{ required: true }]}>
          <Select<ChannelType>
            disabled={editing !== null}
            options={CHANNEL_TYPES.map((type) => ({ value: type, label: t(`channels.types.${type}`) }))}
          />
        </Form.Item>
        {/* Rendered (hidden) on creation too, so the dialog has the same fields in both modes: a new channel is active. */}
        <Form.Item
          name="status"
          label={t('channels.form.status')}
          hidden={editing === null}
          rules={[{ required: true }]}
        >
          <Select<ChannelStatus>
            options={CHANNEL_STATUSES.map((status) => ({ value: status, label: t(`channels.status.${status}`) }))}
          />
        </Form.Item>
        <ResetFormOnOpen openCount={openCount} />
      </Form>
    </Modal>
  );
}
