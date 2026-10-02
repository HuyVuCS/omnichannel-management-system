import { Form, Input, Modal, Select } from 'antd';
import { useTranslation } from 'react-i18next';
import { ProductSelect } from '@/features/products/components/ProductSelect';
import { productLabel } from '@/features/products/productsApi';
import { FormErrors } from '@/shared/components/FormErrors';
import { MODAL_WIDTH } from '@/shared/components/layout';
import { useFormSubmission } from '@/shared/hooks/useFormSubmission';
import { useResetFormOnOpen } from '@/shared/hooks/useResetFormOnOpen';
import {
  CHANNEL_PRODUCT_STATUSES,
  useCreateChannelProductMutation,
  useUpdateChannelProductMutation,
  type Channel,
  type ChannelProduct,
  type ChannelProductStatus,
  type SavedChannelProduct,
} from '../channelsApi';

/** What the dialog is open for; null = closed. */
export type ChannelProductFormState = { kind: 'create' } | { kind: 'edit'; mapping: ChannelProduct } | null;

interface ChannelProductFormModalProps {
  /** The channel the mapping belongs to (shown, never editable). */
  channel: Channel;
  state: ChannelProductFormState;
  onClose: () => void;
  /** The API's answer to a successful create or change (it says whether a stock sync was started). */
  onSaved: (saved: SavedChannelProduct) => void;
}

interface ChannelProductFormValues {
  productId: string;
  externalProductId?: string;
  externalSku?: string;
  status: ChannelProductStatus;
}

/** The API's limit for both external ids; here only an input limit. */
const MAX_EXTERNAL_ID_LENGTH = 128;

const CODE_FIELDS = { channel_product_exists: 'productId', external_product_id_taken: 'externalProductId' };

function orNull(value: string | undefined): string | null {
  return value ? value : null;
}

/**
 * Map a product on a channel, or change a mapping (Manager/Admin). The product can only be chosen on creation; the
 * external ids and - when editing - the status can be changed. The status field is the whole (de)activation: nothing is
 * deleted and it can be switched back, so there is no confirmation.
 *
 * The rules are the API's and come back on the fields: at least one of the two external ids (reported on the external
 * product id), a product that is already mapped (on the product), an external product id that belongs to another
 * product (on the external product id).
 */
export function ChannelProductFormModal({ channel, state, onClose, onSaved }: ChannelProductFormModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<ChannelProductFormValues>();
  useResetFormOnOpen(form, state);
  const [createMapping, creating] = useCreateChannelProductMutation();
  const [updateMapping, updating] = useUpdateChannelProductMutation();
  const editing = state?.kind === 'edit' ? state.mapping : null;

  const { submit, formErrors, clearErrors } = useFormSubmission(form, {
    action: async ({ productId, externalProductId, externalSku, status }) => {
      const externalIds = { externalProductId: orNull(externalProductId), externalSku: orNull(externalSku) };
      const saved = await (editing
        ? updateMapping({
            channelId: channel.channelId,
            channelProductId: editing.channelProductId,
            changes: { ...externalIds, status },
          }).unwrap()
        : createMapping({ channelId: channel.channelId, mapping: { productId, ...externalIds } }).unwrap());
      onSaved(saved);
    },
    successKey: editing ? 'channelDetail.messages.updated' : 'channelDetail.messages.created',
    onSuccess: onClose,
    codeFields: CODE_FIELDS,
  });

  const initialValues: Partial<ChannelProductFormValues> = editing
    ? {
        productId: editing.productId,
        externalProductId: editing.externalProductId ?? undefined,
        externalSku: editing.externalSku ?? undefined,
        status: editing.status,
      }
    : { status: 'Active' };

  return (
    <Modal
      open={state !== null}
      title={t(editing ? 'channelDetail.editModal.title' : 'channelDetail.createModal.title')}
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
        // With `useResetFormOnOpen`: a dialog opened again must not start with the values of the previous time.
        clearOnDestroy
        initialValues={initialValues}
        onFinish={(values) => {
          // A field with a rule is validated again on every submit, which removes its old API error. The external ids
          // have no rule here (theirs are the API's): their errors of the previous attempt are removed by hand.
          form.setFields([
            { name: 'externalProductId', errors: [] },
            { name: 'externalSku', errors: [] },
          ]);
          void submit(values);
        }}
      >
        <Form.Item label={t('channelDetail.form.channel')}>
          <Input value={channel.name} disabled />
        </Form.Item>
        {editing ? (
          <Form.Item label={t('channelDetail.form.product')}>
            <Input value={productLabel({ sku: editing.sku, name: editing.productName })} disabled />
          </Form.Item>
        ) : (
          <Form.Item name="productId" label={t('channelDetail.form.product')} rules={[{ required: true }]}>
            <ProductSelect />
          </Form.Item>
        )}
        <Form.Item name="externalProductId" label={t('channelDetail.form.externalProductId')}>
          <Input autoComplete="off" maxLength={MAX_EXTERNAL_ID_LENGTH} />
        </Form.Item>
        <Form.Item name="externalSku" label={t('channelDetail.form.externalSku')}>
          <Input autoComplete="off" maxLength={MAX_EXTERNAL_ID_LENGTH} />
        </Form.Item>
        {/* Rendered (hidden) on creation too, so the dialog has the same fields in both modes: a new mapping is active. */}
        <Form.Item
          name="status"
          label={t('channelDetail.form.status')}
          hidden={editing === null}
          rules={[{ required: true }]}
        >
          <Select<ChannelProductStatus>
            options={CHANNEL_PRODUCT_STATUSES.map((status) => ({
              value: status,
              label: t(`channelDetail.mappingStatus.${status}`),
            }))}
          />
        </Form.Item>
      </Form>
    </Modal>
  );
}
