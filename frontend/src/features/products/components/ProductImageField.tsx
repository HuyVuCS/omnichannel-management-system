import { DeleteOutlined, UploadOutlined } from '@ant-design/icons';
import { App as AntApp, Button, Flex, Typography } from 'antd';
import { useRef, useState, type ChangeEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { problemMessages } from '@/shared/api/problemDetails';
import { FieldHint } from '@/shared/components/FieldHint';
import { SECONDARY_ACTION_WIDTH } from '@/shared/components/layout';
import { IMAGE_CONTENT_TYPES, imageFileProblem, StorageUploadError, uploadToStorage } from '../imageUpload';
import {
  useConfirmProductImageMutation,
  usePresignProductImageMutation,
  useRemoveProductImageMutation,
  type Product,
} from '../productsApi';
import { ProductThumbnail } from './ProductThumbnail';

const PREVIEW_SIZE = 96;

/**
 * What went wrong, kept in a form that follows the language: a message of this client is a translation key; what the
 * API said arrived in a language and stays in it.
 */
type ImageError = { key: string; values?: Record<string, unknown> } | { messages: string[] };

/**
 * The image of an existing product, inside its edit dialog: preview, "choose image" and "remove image". Both act at
 * once - they do not wait for "Save", which is for the product's fields.
 *
 * An upload is three steps under one busy state: the API signs a link (presign), the file goes straight to the object
 * storage with it (never through the API), and the API checks what was stored and makes it the image (confirm). The
 * type and size check before the first step only saves a request; the API decides.
 *
 * Mount it with a key per opening of the dialog: it starts from the image the dialog's product had then.
 */
export function ProductImageField({ product }: { product: Product }) {
  const { t } = useTranslation();
  const { modal, message } = AntApp.useApp();
  const [presign] = usePresignProductImageMutation();
  const [confirm] = useConfirmProductImageMutation();
  const [remove] = useRemoveProductImageMutation();
  // The dialog keeps the product of the row it was opened with; what this block did since is here.
  const [image, setImage] = useState(product.image);
  const [busy, setBusy] = useState<'upload' | 'remove' | null>(null);
  const [error, setError] = useState<ImageError | null>(null);
  const input = useRef<HTMLInputElement>(null);
  const { productId } = product;

  const upload = async (file: File) => {
    const problem = imageFileProblem(file);
    if (problem) {
      setError({ key: `products.image.invalid.${problem}` });
      return;
    }

    setError(null);
    setBusy('upload');
    try {
      const target = await presign({ productId, contentType: file.type, sizeBytes: file.size }).unwrap();
      await uploadToStorage(target, file);
      const updated = await confirm({ productId, objectKey: target.objectKey }).unwrap();
      setImage(updated.image);
      message.success(t('products.image.saved'));
    } catch (failure) {
      if (failure instanceof StorageUploadError) {
        setError(
          failure.status === null
            ? { key: 'products.image.uploadUnreachable' }
            : { key: 'products.image.uploadFailed', values: { status: failure.status } },
        );
      } else {
        // What the user can act on (a rejected file, no permission); network errors and 5xx are reported globally.
        const messages = problemMessages(t, failure);
        setError(messages.length > 0 ? { messages } : null);
      }
    } finally {
      setBusy(null);
    }
  };

  const onFileChosen = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    // Emptied, so that choosing the same file again is a change again.
    event.target.value = '';
    if (file) {
      void upload(file);
    }
  };

  const confirmRemove = () => {
    modal.confirm({
      title: t('products.image.removeConfirmTitle'),
      content: t('products.image.removeConfirmContent'),
      okText: t('products.image.remove'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: async () => {
        setError(null);
        setBusy('remove');
        try {
          await remove({ productId }).unwrap();
          setImage(null);
          message.success(t('products.image.removed'));
        } catch (failure) {
          const messages = problemMessages(t, failure);
          setError(messages.length > 0 ? { messages } : null);
        } finally {
          setBusy(null);
        }
      },
    });
  };

  const errorText = error === null ? null : 'key' in error ? t(error.key, error.values) : error.messages.join(' ');

  return (
    <Flex gap={16} align="flex-start" data-testid="product-image-field">
      <ProductThumbnail image={image} name={product.name} size={PREVIEW_SIZE} />
      <Flex vertical gap={8} style={{ flex: 1, minWidth: 0 }}>
        <Flex gap={8}>
          <Button
            icon={<UploadOutlined />}
            loading={busy === 'upload'}
            disabled={busy === 'remove'}
            onClick={() => input.current?.click()}
            style={{ width: SECONDARY_ACTION_WIDTH }}
          >
            {t('products.image.choose')}
          </Button>
          <Button
            danger
            icon={<DeleteOutlined />}
            loading={busy === 'remove'}
            disabled={image === null || busy === 'upload'}
            onClick={confirmRemove}
            style={{ width: SECONDARY_ACTION_WIDTH }}
          >
            {t('products.image.remove')}
          </Button>
        </Flex>
        {/* Opened by the button above, which is what the keyboard reaches. */}
        <input
          ref={input}
          type="file"
          accept={IMAGE_CONTENT_TYPES.join(',')}
          // Not the `hidden` attribute: the UI library's reset gives inputs a `display` that overrides it.
          style={{ display: 'none' }}
          tabIndex={-1}
          aria-label={t('products.image.choose')}
          data-testid="product-image-input"
          onChange={onFileChosen}
        />
        <FieldHint text={t('products.image.hint')} />
        {errorText !== null && (
          <Typography.Text type="danger" role="alert">
            {errorText}
          </Typography.Text>
        )}
      </Flex>
    </Flex>
  );
}
