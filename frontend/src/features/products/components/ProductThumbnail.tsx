import { PictureOutlined } from '@ant-design/icons';
import { theme } from 'antd';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { linkHasExpired } from '../imageLinks';
import type { ProductImage } from '../productsApi';

/** The thumbnail of a table row. */
export const THUMBNAIL_SIZE = 40;

interface ProductThumbnailProps {
  image: ProductImage | null;
  /** The product's name: the text alternative of its image. */
  name: string;
  size?: number;
  /**
   * The image could not be loaded and its link had expired by then (the page was open for longer than a link lives):
   * the owner fetches its data again, and the fresh link gets its own try. Not called for a link that fails before it
   * expires, and not a second time until an image has loaded in between - so a link that keeps failing asks once.
   */
  onLinkExpired?: () => void;
}

/**
 * The image of a product as a fixed square (cropped to fill it), loaded when it comes near the viewport. A product
 * without an image, or one whose image cannot be loaded (its link expired, the storage is down), shows a placeholder
 * of the same size, so a row never changes its height.
 */
export function ProductThumbnail({ image, name, size = THUMBNAIL_SIZE, onLinkExpired }: ProductThumbnailProps) {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  // The link that failed, not a flag: another link - a new image, a fresh link - gets its own try.
  const [failedUrl, setFailedUrl] = useState<string | null>(null);
  // A fresh link was asked for and no image has loaded since. Whatever the clocks say about the next link, it is not
  // asked for again: a storage that is down must not turn into a chain of requests.
  const askedForFreshLink = useRef(false);

  if (image && failedUrl !== image.url) {
    return (
      <img
        src={image.url}
        alt={name}
        width={size}
        height={size}
        loading="lazy"
        decoding="async"
        onLoad={() => {
          askedForFreshLink.current = false;
        }}
        onError={() => {
          setFailedUrl(image.url);
          if (onLinkExpired && !askedForFreshLink.current && linkHasExpired(image)) {
            askedForFreshLink.current = true;
            onLinkExpired();
          }
        }}
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
