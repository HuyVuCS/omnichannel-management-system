import { PictureOutlined } from '@ant-design/icons';
import { theme } from 'antd';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { ProductImage } from '../productsApi';

/** The thumbnail of a table row. */
export const THUMBNAIL_SIZE = 40;

interface ProductThumbnailProps {
  image: ProductImage | null;
  /** The product's name: the text alternative of its image. */
  name: string;
  size?: number;
}

/**
 * The image of a product as a fixed square (cropped to fill it), loaded when it comes near the viewport. A product
 * without an image, or one whose image cannot be loaded (its link expired, the storage is down), shows a placeholder
 * of the same size, so a row never changes its height.
 */
export function ProductThumbnail({ image, name, size = THUMBNAIL_SIZE }: ProductThumbnailProps) {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  // The link that failed, not a flag: another link - a new image, a fresh link - gets its own try.
  const [failedUrl, setFailedUrl] = useState<string | null>(null);

  if (image && failedUrl !== image.url) {
    return (
      <img
        src={image.url}
        alt={name}
        width={size}
        height={size}
        loading="lazy"
        decoding="async"
        onError={() => setFailedUrl(image.url)}
        style={{ display: 'block', objectFit: 'cover', borderRadius: token.borderRadiusSM }}
      />
    );
  }

  return (
    <span
      role="img"
      aria-label={t(image ? 'products.image.unavailable' : 'products.image.none')}
      data-testid="product-image-placeholder"
      style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        width: size,
        height: size,
        borderRadius: token.borderRadiusSM,
        background: token.colorFillTertiary,
        color: token.colorTextQuaternary,
        fontSize: Math.round(size / 2),
      }}
    >
      <PictureOutlined aria-hidden />
    </span>
  );
}
