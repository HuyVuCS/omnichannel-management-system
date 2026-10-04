/** What the API accepts as a product image. Repeated here only to save a request: the API stays the authority. */
export const IMAGE_CONTENT_TYPES = ['image/jpeg', 'image/png', 'image/webp'] as const;

/** 5 MiB, the API's default limit. */
export const MAX_IMAGE_BYTES = 5 * 1024 * 1024;

export type ImageFileProblem = 'type' | 'empty' | 'size';

/** Why a chosen file cannot be a product image, or null when it can be offered to the API. */
export function imageFileProblem(file: Pick<File, 'type' | 'size'>): ImageFileProblem | null {
  if (!(IMAGE_CONTENT_TYPES as readonly string[]).includes(file.type)) {
    return 'type';
  }

  if (file.size === 0) {
    return 'empty';
  }

  return file.size > MAX_IMAGE_BYTES ? 'size' : null;
}

/** What `POST /api/products/{id}/image/presign` answers: where and how to upload. */
export interface UploadTarget {
  uploadUrl: string;
  method: string;
  /** Exactly the headers the upload must carry (the storage checks them against the signature). */
  headers: Record<string, string>;
}

/** The storage did not take the upload: it answered with `status`, or could not be reached (`null`). */
export class StorageUploadError extends Error {
  readonly status: number | null;

  constructor(status: number | null) {
    super(status === null ? 'The storage could not be reached.' : `The storage answered ${status}.`);
    this.name = 'StorageUploadError';
    this.status = status;
  }
}

/**
 * Uploads a file straight to the object storage with a presigned link. Plain `fetch`, deliberately not the API's base
 * query: the storage is another origin, and the link is the credential - the API's access token, the correlation id
 * and any cookie stay out of the request. Only the headers the presign answer names are sent.
 */
export async function uploadToStorage(target: UploadTarget, file: Blob): Promise<void> {
  let response: Response;
  try {
    response = await fetch(target.uploadUrl, {
      method: target.method,
      headers: target.headers,
      body: await file.arrayBuffer(),
      credentials: 'omit',
      referrerPolicy: 'no-referrer',
    });
  } catch {
    throw new StorageUploadError(null);
  }

  if (!response.ok) {
    throw new StorageUploadError(response.status);
  }
}
