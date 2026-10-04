import { http, HttpResponse } from 'msw';
import type { PresignedImageUpload, Product, ProductImage } from '@/features/products/productsApi';
import { api, server } from './server';

/**
 * The object storage of the tests: another origin than the API, as in every deployment. Uploads go there with a
 * presigned link; an image `src` points there (jsdom never loads images, so nothing is requested for them).
 */
export const STORAGE_ORIGIN = 'http://storage.test';

/** One request the storage received. */
export interface StorageUpload {
  method: string;
  url: string;
  /** Every header of the request, lower-case names. */
  headers: Record<string, string>;
  /** Length of the body in bytes. */
  size: number;
}

/**
 * `PUT` to the storage origin: records every upload and answers 200 unless the test replaces `answer`. Requests to
 * this origin are counted by the unanswered-request guard like any other (`pendingRequests.ts`).
 */
export function serveStorage() {
  const uploads: StorageUpload[] = [];
  const state: { answer: () => Response | Promise<Response> } = {
    answer: () => new HttpResponse(null, { status: 200 }),
  };
  server.use(
    http.put(`${STORAGE_ORIGIN}/*`, async ({ request }) => {
      uploads.push({
        method: request.method,
        url: request.url,
        headers: Object.fromEntries(request.headers),
        size: (await request.arrayBuffer()).byteLength,
      });
      return state.answer();
    }),
  );
  return { uploads, state };
}

/** A download link as the API signs it: the key plus a signature that differs per response. */
export function imageUrl(key: string, signature: string): string {
  return `${STORAGE_ORIGIN}/omnichannel-test/${key}?X-Amz-Signature=${signature}`;
}

/** An image of a product; valid for 15 minutes from now unless `expiresInMs` says otherwise. */
export function productImage(
  overrides: Partial<ProductImage> = {},
  expiresInMs: number = 15 * 60 * 1000,
): ProductImage {
  return {
    url: imageUrl('products/p/1', 'sig-1'),
    expiresAt: new Date(Date.now() + expiresInMs).toISOString(),
    contentType: 'image/png',
    sizeBytes: 2048,
    updatedAt: '2026-10-04T02:00:00+00:00',
    ...overrides,
  };
}

interface ImageBackend {
  /** Every presign request: the product and what the client declared. */
  presigns: { productId: string; contentType: string; sizeBytes: number }[];
  /** Every confirmation: the product and the key. */
  confirms: { productId: string; objectKey: string }[];
  /** The product of every remove request. */
  removes: string[];
  presignAnswer?: () => Response;
  confirmAnswer?: () => Response;
  removeAnswer?: () => Response;
}

/**
 * The three image endpoints of the API over a product list the test owns (`products`, changed in place: a confirmed
 * upload gives the product an image, a removal takes it away - the next list request shows it).
 */
export function serveProductImages(products: { products: Product[] }) {
  const backend: ImageBackend = { presigns: [], confirms: [], removes: [] };
  let uploadNo = 0;
  const update = (productId: string, image: ProductImage | null) => {
    products.products = products.products.map((item) => (item.productId === productId ? { ...item, image } : item));
    return products.products.find((item) => item.productId === productId);
  };

  server.use(
    http.post(api('/products/:id/image/presign'), async ({ request, params }) => {
      const productId = String(params.id);
      const body = (await request.json()) as { contentType: string; sizeBytes: number };
      backend.presigns.push({ productId, ...body });
      if (backend.presignAnswer) {
        return backend.presignAnswer();
      }

      uploadNo += 1;
      const objectKey = `products/${productId}/upload-${uploadNo}`;
      return HttpResponse.json<PresignedImageUpload>({
        objectKey,
        uploadUrl: `${STORAGE_ORIGIN}/omnichannel-test/${objectKey}?X-Amz-Signature=upload-${uploadNo}`,
        method: 'PUT',
        headers: { 'Content-Type': body.contentType },
        expiresAt: new Date(Date.now() + 5 * 60 * 1000).toISOString(),
      });
    }),
    http.put(api('/products/:id/image'), async ({ request, params }) => {
      const productId = String(params.id);
      const { objectKey } = (await request.json()) as { objectKey: string };
      backend.confirms.push({ productId, objectKey });
      if (backend.confirmAnswer) {
        return backend.confirmAnswer();
      }

      const presigned = backend.presigns.at(-1);
      const updated = update(
        productId,
        productImage({
          url: imageUrl(objectKey, `confirmed-${backend.confirms.length}`),
          contentType: presigned?.contentType ?? 'image/png',
          sizeBytes: presigned?.sizeBytes ?? 1,
          updatedAt: new Date(Date.UTC(2026, 9, 4, 3, 0, backend.confirms.length)).toISOString(),
        }),
      );
      return HttpResponse.json(updated);
    }),
    http.delete(api('/products/:id/image'), ({ params }) => {
      const productId = String(params.id);
      backend.removes.push(productId);
      if (backend.removeAnswer) {
        return backend.removeAnswer();
      }

      update(productId, null);
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return backend;
}
