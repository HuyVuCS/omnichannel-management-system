import type { ProductImage } from './productsApi';

/** A link is used again until this long before it expires. */
export const REUSE_MARGIN_MS = 60_000;

/** The link in use per product, with the version of the image (`updatedAt`) it belongs to. */
const linksInUse = new Map<string, Pick<ProductImage, 'updatedAt' | 'url' | 'expiresAt'>>();

/**
 * The API signs a new download link for every response, and lists are fetched again whenever stock changes: a new
 * `src` each time would make the browser load every image again. So the link this client already has for the same
 * image (same product, same `updatedAt`) is kept until shortly before it expires; a new image or a link about to
 * expire takes the new one.
 */
export function withStableUrl(
  productId: string,
  image: ProductImage | null,
  now: number = Date.now(),
): ProductImage | null {
  if (!image) {
    linksInUse.delete(productId);
    return null;
  }

  const inUse = linksInUse.get(productId);
  if (inUse && inUse.updatedAt === image.updatedAt && Date.parse(inUse.expiresAt) - now > REUSE_MARGIN_MS) {
    return { ...image, url: inUse.url, expiresAt: inUse.expiresAt };
  }

  linksInUse.set(productId, { updatedAt: image.updatedAt, url: image.url, expiresAt: image.expiresAt });
  return image;
}

/**
 * True once a link is past its `expiresAt`: the storage refuses it from then on, and the API signs a usable one with
 * its next response. An image that fails to load before that failed for another reason (the storage is down, the
 * object is broken) - asking the API again would bring a link that fails the same way.
 */
export function linkHasExpired(image: Pick<ProductImage, 'expiresAt'>, now: number = Date.now()): boolean {
  return Date.parse(image.expiresAt) <= now;
}

/** Forgets every link (the session ended: the next user starts with the links of their own responses). */
export function forgetImageLinks(): void {
  linksInUse.clear();
}
