import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import { baseApi } from '@/shared/api/baseApi';
import type { PagedResult } from '@/shared/api/types';
import { findDialog, rowOf, settled, switchLanguage, type AppUser } from '@/test/interactions';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';
import { imageUrl, productImage, serveProductImages, serveStorage, STORAGE_ORIGIN } from '@/test/storage';
import { forgetImageLinks, REUSE_MARGIN_MS, withStableUrl } from './imageLinks';
import { MAX_IMAGE_BYTES } from './imageUpload';
import type { Product } from './productsApi';

const base = {
  price: 10000,
  lowStockThreshold: 5,
  expiryDate: null,
  totalStock: 0,
  reservedStock: 0,
  availableStock: 0,
  isLowStock: false,
  createdAt: '2026-09-30T03:15:00+00:00',
  updatedAt: null,
};
const teaImage = productImage({ url: imageUrl('products/p-tea/1', 'sig-1') });
const tea: Product = { ...base, productId: 'p-tea', sku: 'TEA-01', name: 'Trà xanh', image: teaImage };
const coffee: Product = { ...base, productId: 'p-coffee', sku: 'COF-01', name: 'Cà phê', image: null };

/** `GET /api/products` over a list the test (and `serveProductImages`) changes in place. */
function serveProducts(initial: Product[]) {
  const state = { products: initial };
  const requests: Record<string, string>[] = [];
  server.use(
    http.get(api('/products'), ({ request }) => {
      const url = new URL(request.url);
      requests.push(Object.fromEntries(url.searchParams));
      const page: PagedResult<Product> = {
        items: state.products,
        page: 1,
        pageSize: 20,
        totalCount: state.products.length,
      };
      return HttpResponse.json(page);
    }),
  );
  return { state, requests };
}

function renderProducts(user: AuthUser = managerUser) {
  return renderApp({ route: '/products', session: sessionOf(user) });
}

/** The products page of a Manager with tea (has an image) and coffee (has none), the storage and the image endpoints. */
async function renderWithImages(user: AuthUser = managerUser) {
  const products = serveProducts([tea, coffee]);
  const images = serveProductImages(products.state);
  const storage = serveStorage();
  const app = renderProducts(user);
  await screen.findByText('TEA-01');
  return { ...app, products, images, storage };
}

async function openEditDialog(appUser: AppUser, sku: string) {
  await appUser.click(within(rowOf(sku)).getByRole('button', { name: 'Sửa' }));
  return findDialog('Sửa sản phẩm');
}

const png = (name = 'tea.png', size = 2048) => new File([new Uint8Array(size)], name, { type: 'image/png' });

/** What the file dialog does when the user picks a file (the `accept` filter of the dialog is not applied here). */
function chooseFile(dialog: HTMLElement, file: File) {
  fireEvent.change(within(dialog).getByTestId('product-image-input'), { target: { files: [file] } });
}

/** The image the row or the dialog shows, or null when it shows a placeholder. */
const imageIn = (container: HTMLElement) => container.querySelector('img');
const placeholderIn = (container: HTMLElement) => within(container).queryByTestId('product-image-placeholder');

/** The list is fetched again, as after any stock change. */
async function refetchList(store: ReturnType<typeof renderApp>['store'], requests: unknown[]) {
  const before = requests.length;
  act(() => {
    store.dispatch(baseApi.util.invalidateTags([{ type: 'Product', id: 'LIST' }]));
  });
  await waitFor(() => expect(requests).toHaveLength(before + 1));
  await settled(store);
}

// The links in use are remembered per page load (module state): every test starts like a fresh page.
afterEach(forgetImageLinks);

describe('product images: list', () => {
  it('shows a thumbnail per product: the image with the product name as its text, or a placeholder', async () => {
    serveProducts([tea, coffee]);
    renderProducts();
    await screen.findByText('TEA-01');

    const image = screen.getByRole('img', { name: 'Trà xanh' });
    expect(image.tagName).toBe('IMG');
    expect(image).toHaveAttribute('src', teaImage.url);
    expect(image).toHaveAttribute('loading', 'lazy');
    expect(image).toHaveAttribute('width', '40');
    expect(image).toHaveAttribute('height', '40');
    expect(image).toHaveStyle({ objectFit: 'cover' });
    expect(rowOf('TEA-01')).toContainElement(image);

    // No image: a placeholder of the same size, named as what it is.
    const placeholder = within(rowOf('COF-01')).getByRole('img', { name: 'Chưa có ảnh' });
    expect(placeholder).toHaveStyle({ width: '40px', height: '40px' });
    expect(imageIn(rowOf('COF-01'))).toBeNull();
    expect([...document.querySelectorAll('thead th')].map((th) => th.textContent)[0]).toBe('Ảnh');

    await settled();
  });

  it('shows the placeholder when the image cannot be loaded, and tries a new link again', async () => {
    const { state, requests } = serveProducts([tea, coffee]);
    const { store } = renderProducts();
    await screen.findByText('TEA-01');

    fireEvent.error(screen.getByRole('img', { name: 'Trà xanh' }));

    const placeholder = within(rowOf('TEA-01')).getByRole('img', { name: 'Không tải được ảnh' });
    expect(placeholder).toHaveStyle({ width: '40px', height: '40px' });
    expect(imageIn(rowOf('TEA-01'))).toBeNull();

    // A new image of the product: its link has not failed.
    const replaced = productImage({
      url: imageUrl('products/p-tea/2', 'sig-2'),
      updatedAt: '2026-10-04T05:00:00+00:00',
    });
    state.products = [{ ...tea, image: replaced }, coffee];
    await refetchList(store, requests);
    expect(screen.getByRole('img', { name: 'Trà xanh' })).toHaveAttribute('src', replaced.url);
  });

  it.each([
    ['Staff', staffUser, false],
    ['Manager', managerUser, true],
  ] as const)('%s sees the thumbnails; image controls only with the edit dialog: %s', async (_role, user, canWrite) => {
    serveProducts([tea, coffee]);
    renderProducts(user);
    await screen.findByText('TEA-01');

    expect(screen.getByRole('img', { name: 'Trà xanh' })).toHaveAttribute('src', teaImage.url);
    expect(within(rowOf('COF-01')).getByRole('img', { name: 'Chưa có ảnh' })).toBeInTheDocument();
    // The only way to the image controls is the edit dialog, which Staff do not have.
    expect(screen.queryAllByRole('button', { name: 'Sửa' })).toHaveLength(canWrite ? 2 : 0);
    expect(screen.queryByTestId('product-image-input')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Chọn ảnh|Xoá ảnh/ })).not.toBeInTheDocument();

    await settled();
  });
});

describe('product images: one link per image', () => {
  const newLink = (signature: string, overrides = {}) =>
    productImage({ url: imageUrl('products/p-tea/1', signature), ...overrides });

  it('keeps the src when the list comes again with a new link for the same image', async () => {
    const { state, requests } = serveProducts([tea, coffee]);
    const { store } = renderProducts();
    await screen.findByText('TEA-01');
    const image = screen.getByRole('img', { name: 'Trà xanh' });

    // The API signs a new link for every response.
    state.products = [{ ...tea, image: newLink('sig-2') }, coffee];
    await refetchList(store, requests);
    state.products = [{ ...tea, image: newLink('sig-3') }, coffee];
    await refetchList(store, requests);

    expect(screen.getByRole('img', { name: 'Trà xanh' })).toHaveAttribute('src', teaImage.url);
    // The same element: the browser has nothing to load again.
    expect(screen.getByRole('img', { name: 'Trà xanh' })).toBe(image);
  });

  it('takes the new link when the image is another one', async () => {
    const { state, requests } = serveProducts([tea, coffee]);
    const { store } = renderProducts();
    await screen.findByText('TEA-01');

    const replaced = productImage({
      url: imageUrl('products/p-tea/2', 'sig-2'),
      updatedAt: '2026-10-04T05:00:00+00:00',
    });
    state.products = [{ ...tea, image: replaced }, coffee];
    await refetchList(store, requests);

    expect(screen.getByRole('img', { name: 'Trà xanh' })).toHaveAttribute('src', replaced.url);
  });

  it('takes the new link when the one in use is about to expire', async () => {
    // In use: a link with less than the reuse margin left.
    const expiring = productImage({ url: imageUrl('products/p-tea/1', 'sig-old') }, REUSE_MARGIN_MS - 5000);
    const { state, requests } = serveProducts([{ ...tea, image: expiring }, coffee]);
    const { store } = renderProducts();
    await screen.findByText('TEA-01');
    expect(screen.getByRole('img', { name: 'Trà xanh' })).toHaveAttribute('src', expiring.url);

    const fresh = newLink('sig-fresh');
    state.products = [{ ...tea, image: fresh }, coffee];
    await refetchList(store, requests);

    expect(screen.getByRole('img', { name: 'Trà xanh' })).toHaveAttribute('src', fresh.url);
  });

  it('decides by product, image version and the time left (the rule itself)', () => {
    const now = Date.parse('2026-10-04T02:00:00Z');
    const at = (ms: number) => new Date(now + ms).toISOString();
    const first = { ...teaImage, url: 'first', expiresAt: at(15 * 60_000) };
    const second = { ...teaImage, url: 'second', expiresAt: at(30 * 60_000) };

    expect(withStableUrl('p-1', first, now)).toEqual(first);
    // Same image, a later response: the link in use and its own expiry.
    expect(withStableUrl('p-1', second, now + 60_000)).toEqual({ ...second, url: 'first', expiresAt: first.expiresAt });
    // Exactly the margin left is not enough; one millisecond more is.
    expect(withStableUrl('p-1', second, now + 14 * 60_000 - 1)?.url).toBe('first');
    expect(withStableUrl('p-1', second, now + 14 * 60_000)?.url).toBe('second');
    // From now on the second link is the one in use.
    expect(withStableUrl('p-1', { ...second, url: 'third' }, now + 15 * 60_000)?.url).toBe('second');
    // Another product never shares a link.
    expect(withStableUrl('p-2', second, now)?.url).toBe('second');
    // Another version of the image.
    expect(withStableUrl('p-1', { ...first, url: 'new', updatedAt: at(1) }, now + 15 * 60_000)?.url).toBe('new');
    // No image any more; a later image starts over even with the old version stamp.
    expect(withStableUrl('p-1', null, now)).toBeNull();
    expect(withStableUrl('p-1', { ...first, url: 'again', updatedAt: at(1) }, now)?.url).toBe('again');
  });
});

describe('product images: upload', () => {
  it('uploads in three steps - presign, PUT to the storage, confirm - and refreshes the list', async () => {
    const { user, products, images, storage } = await renderWithImages();
    const dialog = await openEditDialog(user, 'COF-01');
    const field = within(dialog).getByTestId('product-image-field');
    expect(within(field).getByRole('img', { name: 'Chưa có ảnh' })).toBeInTheDocument();
    expect(within(field).getByText('JPEG, PNG hoặc WebP, tối đa 5 MB.')).toBeInTheDocument();
    expect(within(dialog).getByTestId('product-image-input')).toHaveAttribute(
      'accept',
      'image/jpeg,image/png,image/webp',
    );

    chooseFile(dialog, png('coffee.png', 3000));

    expect(await screen.findByText('Đã lưu ảnh.')).toBeInTheDocument();
    expect(images.presigns).toEqual([{ productId: 'p-coffee', contentType: 'image/png', sizeBytes: 3000 }]);
    // The upload: to the storage, the method and exactly the headers the presign answer named, the file as body.
    expect(storage.uploads).toHaveLength(1);
    const upload = storage.uploads[0]!;
    expect(upload.method).toBe('PUT');
    expect(upload.url).toBe(`${STORAGE_ORIGIN}/omnichannel-test/products/p-coffee/upload-1?X-Amz-Signature=upload-1`);
    expect(upload.headers['content-type']).toBe('image/png');
    expect(upload.size).toBe(3000);
    // Nothing of the API goes to the storage: the link is the credential.
    expect(upload.headers).not.toHaveProperty('authorization');
    expect(upload.headers).not.toHaveProperty('x-correlation-id');
    // (`Accept-Language: *` is what `fetch` itself sends outside a browser; the API's requests carry the UI language.)
    expect(upload.headers['accept-language']).not.toBe('vi');
    expect(upload.headers).not.toHaveProperty('cookie');
    expect(images.confirms).toEqual([{ productId: 'p-coffee', objectKey: 'products/p-coffee/upload-1' }]);

    // The dialog shows the new image at once; the list was fetched again and shows it in the row.
    const confirmed = products.state.products.find((item) => item.productId === 'p-coffee')!.image!;
    expect(within(field).getByRole('img', { name: 'Cà phê' })).toHaveAttribute('src', confirmed.url);
    await waitFor(() => expect(products.requests).toHaveLength(2));
    await waitFor(() => expect(imageIn(rowOf('COF-01'))).toHaveAttribute('src', confirmed.url));
    expect(within(field).getByRole('button', { name: /Xoá ảnh/ })).toBeEnabled();

    await settled();
  });

  it('is busy from the first step to the last: one loading state, the other action disabled', async () => {
    const { user, images, storage } = await renderWithImages();
    let release: (() => void) | undefined;
    storage.state.answer = () =>
      new Promise<Response>((resolve) => {
        release = () => resolve(new HttpResponse(null, { status: 200 }));
      });
    const dialog = await openEditDialog(user, 'TEA-01');

    chooseFile(dialog, png());

    await waitFor(() => expect(storage.uploads).toHaveLength(1));
    expect(within(dialog).getByRole('button', { name: /Chọn ảnh/ })).toHaveClass('ant-btn-loading');
    expect(within(dialog).getByRole('button', { name: /Xoá ảnh/ })).toBeDisabled();
    expect(images.confirms).toEqual([]);

    release?.();
    expect(await screen.findByText('Đã lưu ảnh.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: /Chọn ảnh/ })).not.toHaveClass('ant-btn-loading');
    expect(within(dialog).getByRole('button', { name: /Xoá ảnh/ })).toBeEnabled();

    await settled();
  });

  it.each([
    [
      'another type',
      new File(['<svg/>'], 'logo.svg', { type: 'image/svg+xml' }),
      'Tệp phải là ảnh JPEG, PNG hoặc WebP.',
    ],
    ['a GIF', new File([new Uint8Array(10)], 'a.gif', { type: 'image/gif' }), 'Tệp phải là ảnh JPEG, PNG hoặc WebP.'],
    ['no type', new File([new Uint8Array(10)], 'file', { type: '' }), 'Tệp phải là ảnh JPEG, PNG hoặc WebP.'],
    ['too large', png('big.png', MAX_IMAGE_BYTES + 1), 'Tệp lớn hơn 5 MB.'],
    ['empty', png('empty.png', 0), 'Tệp rỗng.'],
  ])('rejects a file the API would refuse without asking it (%s)', async (_name, file, message) => {
    const { user, products, images, storage } = await renderWithImages();
    const dialog = await openEditDialog(user, 'TEA-01');

    chooseFile(dialog, file);

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(message);
    expect(images.presigns).toEqual([]);
    expect(storage.uploads).toEqual([]);
    expect(products.requests).toHaveLength(1);
    // The image the product has is untouched.
    expect(imageIn(within(dialog).getByTestId('product-image-field'))).toHaveAttribute('src', teaImage.url);
  });

  it('accepts a file of exactly the largest size and each allowed type', async () => {
    const { user, images } = await renderWithImages();
    const dialog = await openEditDialog(user, 'TEA-01');

    chooseFile(dialog, new File([new Uint8Array(MAX_IMAGE_BYTES)], 'max.jpg', { type: 'image/jpeg' }));
    await waitFor(() => expect(images.confirms).toHaveLength(1));
    chooseFile(dialog, new File([new Uint8Array(10)], 'small.webp', { type: 'image/webp' }));
    await waitFor(() => expect(images.confirms).toHaveLength(2));

    expect(images.presigns.map((presign) => [presign.contentType, presign.sizeBytes])).toEqual([
      ['image/jpeg', MAX_IMAGE_BYTES],
      ['image/webp', 10],
    ]);
    expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument();

    await settled();
  });

  it('shows what the API says about the declared file (presign 400) and uploads nothing', async () => {
    const { user, images, storage } = await renderWithImages();
    images.presignAnswer = () =>
      problem(400, {
        errorCode: 'validation_failed',
        errors: { sizeBytes: ['Ảnh không được lớn hơn 2 MB. (máy chủ)'] },
      });
    const dialog = await openEditDialog(user, 'TEA-01');

    chooseFile(dialog, png());

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Ảnh không được lớn hơn 2 MB. (máy chủ)');
    expect(storage.uploads).toEqual([]);
    expect(images.confirms).toEqual([]);
    expect(within(dialog).getByRole('button', { name: /Chọn ảnh/ })).toBeEnabled();
  });

  it('says so with the status when the storage refuses the upload (an expired link), and confirms nothing', async () => {
    const { user, products, images, storage } = await renderWithImages();
    storage.state.answer = () => new HttpResponse('<Error><Code>AccessDenied</Code></Error>', { status: 403 });
    const dialog = await openEditDialog(user, 'TEA-01');

    chooseFile(dialog, png());

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Kho lưu trữ không nhận tệp tải lên (HTTP 403). Vui lòng thử lại.',
    );
    expect(storage.uploads).toHaveLength(1);
    expect(images.confirms).toEqual([]);
    expect(products.requests).toHaveLength(1);
    // Not an error of the API: no global notification.
    expect(document.querySelector('.ant-notification-notice')).toBeNull();

    // Trying again works, and the message goes away.
    storage.state.answer = () => new HttpResponse(null, { status: 200 });
    chooseFile(dialog, png());
    expect(await screen.findByText('Đã lưu ảnh.')).toBeInTheDocument();
    expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument();

    await settled();
  });

  it('says that the storage cannot be reached when the upload fails without an answer', async () => {
    const { user, images, storage } = await renderWithImages();
    storage.state.answer = () => HttpResponse.error();
    const dialog = await openEditDialog(user, 'TEA-01');

    chooseFile(dialog, png());

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Không kết nối được kho lưu trữ. Hãy kiểm tra kết nối và thử lại.',
    );
    expect(images.confirms).toEqual([]);
  });

  it('shows the field error of the confirmation at the image block and keeps the image the product has', async () => {
    const { user, products, images } = await renderWithImages();
    images.confirmAnswer = () =>
      problem(400, {
        errorCode: 'validation_failed',
        errors: { objectKey: ['Nội dung tệp không phải là ảnh thuộc loại đã khai báo khi tải lên.'] },
      });
    const dialog = await openEditDialog(user, 'TEA-01');

    chooseFile(dialog, png());

    const alert = await within(dialog).findByRole('alert');
    expect(alert).toHaveTextContent('Nội dung tệp không phải là ảnh thuộc loại đã khai báo khi tải lên.');
    expect(within(dialog).getByTestId('product-image-field')).toContainElement(alert);
    expect(images.confirms).toHaveLength(1);
    expect(imageIn(within(dialog).getByTestId('product-image-field'))).toHaveAttribute('src', teaImage.url);
    // A rejected confirmation changed nothing: the list is not fetched again.
    expect(products.requests).toHaveLength(1);
    expect(screen.queryByText('Đã lưu ảnh.')).not.toBeInTheDocument();
  });

  it('reports a storage outage of the API (503) globally with the reference code, not in the block', async () => {
    const { user, products, images } = await renderWithImages();
    images.confirmAnswer = () => problem(503, { errorCode: 'storage_unavailable', correlationId: 'corr-storage-1' });
    const dialog = await openEditDialog(user, 'TEA-01');

    chooseFile(dialog, png());

    expect(
      await screen.findByText('Hiện không kết nối được kho lưu trữ tệp. Vui lòng thử lại sau giây lát.'),
    ).toBeInTheDocument();
    expect(screen.getByTestId('correlation-id')).toHaveTextContent('corr-storage-1');
    expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: /Chọn ảnh/ })).toBeEnabled();
    expect(products.requests).toHaveLength(1);
  });

  it('says in the UI language that the role may not upload (403 of the API)', async () => {
    const { user, images, storage } = await renderWithImages();
    images.presignAnswer = () => problem(403);
    const dialog = await openEditDialog(user, 'TEA-01');

    chooseFile(dialog, png());

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Bạn không có quyền thực hiện thao tác này.');
    expect(storage.uploads).toEqual([]);
  });
});

describe('product images: remove', () => {
  it('asks first; cancelling asks the API for nothing', async () => {
    const { user, images } = await renderWithImages();
    const dialog = await openEditDialog(user, 'TEA-01');

    await user.click(within(dialog).getByRole('button', { name: /Xoá ảnh/ }));
    const confirmation = await findDialog('Xoá ảnh của sản phẩm này?');
    expect(confirmation).toHaveTextContent('Ảnh sẽ bị xoá và không thể khôi phục.');
    await user.click(within(confirmation).getByRole('button', { name: 'Huỷ' }));

    await waitFor(() => expect(screen.queryByText('Xoá ảnh của sản phẩm này?')).not.toBeInTheDocument());
    expect(images.removes).toEqual([]);
    expect(imageIn(within(dialog).getByTestId('product-image-field'))).toHaveAttribute('src', teaImage.url);
  });

  it('removes after the confirmation: placeholder in the dialog and in the row, nothing left to remove', async () => {
    const { user, products, images } = await renderWithImages();
    const dialog = await openEditDialog(user, 'TEA-01');
    const field = within(dialog).getByTestId('product-image-field');

    await user.click(within(dialog).getByRole('button', { name: /Xoá ảnh/ }));
    const confirmation = await findDialog('Xoá ảnh của sản phẩm này?');
    await user.click(within(confirmation).getByRole('button', { name: /Xoá ảnh/ }));

    expect(await screen.findByText('Đã xoá ảnh.')).toBeInTheDocument();
    expect(images.removes).toEqual(['p-tea']);
    expect(within(field).getByRole('img', { name: 'Chưa có ảnh' })).toBeInTheDocument();
    expect(within(field).getByRole('button', { name: /Xoá ảnh/ })).toBeDisabled();
    await waitFor(() => expect(products.requests).toHaveLength(2));
    await waitFor(() => expect(placeholderIn(rowOf('TEA-01'))).toBeInTheDocument());

    await settled();
  });

  it('cannot remove what is not there', async () => {
    const { user } = await renderWithImages();
    const dialog = await openEditDialog(user, 'COF-01');

    expect(within(dialog).getByRole('button', { name: /Xoá ảnh/ })).toBeDisabled();
    expect(within(dialog).getByRole('button', { name: /Chọn ảnh/ })).toBeEnabled();
  });

  it('keeps the image and says why when the API refuses', async () => {
    const { user, images } = await renderWithImages();
    images.removeAnswer = () => problem(404, { errorCode: 'not_found' });
    const dialog = await openEditDialog(user, 'TEA-01');

    await user.click(within(dialog).getByRole('button', { name: /Xoá ảnh/ }));
    await user.click(within(await findDialog('Xoá ảnh của sản phẩm này?')).getByRole('button', { name: /Xoá ảnh/ }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Không tìm thấy dữ liệu.');
    expect(imageIn(within(dialog).getByTestId('product-image-field'))).toHaveAttribute('src', teaImage.url);
    expect(screen.queryByText('Đã xoá ảnh.')).not.toBeInTheDocument();
  });
});

describe('product images: the dialog', () => {
  it('says in the create dialog that the image comes after saving, and offers no upload there', async () => {
    serveProducts([tea, coffee]);
    const { user } = renderProducts();
    await screen.findByText('TEA-01');

    await user.click(screen.getByRole('button', { name: /Thêm sản phẩm/ }));
    const dialog = await findDialog('Thêm sản phẩm');

    expect(within(dialog).getByText('Ảnh được thêm sau khi đã lưu sản phẩm.')).toBeInTheDocument();
    expect(within(dialog).queryByTestId('product-image-input')).not.toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: /Chọn ảnh|Xoá ảnh/ })).not.toBeInTheDocument();
  });

  it('reaches both actions with the keyboard; the file input itself is out of the tab order', async () => {
    const { user } = await renderWithImages();
    const dialog = await openEditDialog(user, 'TEA-01');
    const choose = within(dialog).getByRole('button', { name: /Chọn ảnh/ });
    const remove = within(dialog).getByRole('button', { name: /Xoá ảnh/ });
    const input = within(dialog).getByTestId<HTMLInputElement>('product-image-input');
    let opened = 0;
    input.addEventListener('click', () => (opened += 1));

    choose.focus();
    await user.keyboard('{Enter}');
    expect(opened).toBe(1);
    await user.tab();
    expect(remove).toHaveFocus();
    expect(input).toHaveAttribute('tabindex', '-1');
    expect(input).not.toBeVisible();

    await user.keyboard('{Enter}');
    expect(await findDialog('Xoá ảnh của sản phẩm này?')).toBeInTheDocument();
  });

  it('starts from the product of the row at every opening: another product, and the same one after its image changed', async () => {
    const { user, products } = await renderWithImages();

    let dialog = await openEditDialog(user, 'COF-01');
    chooseFile(dialog, png('coffee.png'));
    expect(await screen.findByText('Đã lưu ảnh.')).toBeInTheDocument();
    const coffeeImage = products.state.products.find((item) => item.productId === 'p-coffee')!.image!;
    await user.click(within(dialog).getByRole('button', { name: 'Huỷ' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await waitFor(() => expect(imageIn(rowOf('COF-01'))).toHaveAttribute('src', coffeeImage.url));

    // Another product: its own image, not the one just uploaded.
    dialog = await openEditDialog(user, 'TEA-01');
    expect(imageIn(within(dialog).getByTestId('product-image-field'))).toHaveAttribute('src', teaImage.url);
    expect(within(dialog).getByRole('img', { name: 'Trà xanh' })).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Huỷ' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    // The first product again: the image it has now.
    dialog = await openEditDialog(user, 'COF-01');
    expect(imageIn(within(dialog).getByTestId('product-image-field'))).toHaveAttribute('src', coffeeImage.url);

    await settled();
  });

  it('changes only text when the language changes: the message follows, the block and what was typed stay', async () => {
    const { user, images } = await renderWithImages(adminUser);
    const dialog = await openEditDialog(user, 'TEA-01');
    const name = within(dialog).getByLabelText('Tên sản phẩm');
    await user.clear(name);
    await user.type(name, 'Trà đang sửa');
    chooseFile(dialog, png('big.png', MAX_IMAGE_BYTES + 1));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Tệp lớn hơn 5 MB.');
    const field = within(dialog).getByTestId('product-image-field');
    const preview = imageIn(field);
    const input = within(dialog).getByTestId('product-image-input');
    const buttonWidths = () => [...field.querySelectorAll('button')].map((button) => button.style.width);
    const widths = buttonWidths();

    await switchLanguage(user, 'EN');

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('The file is larger than 5 MB.');
    expect(within(dialog).getByRole('button', { name: /Choose image/ })).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: /Remove image/ })).toBeInTheDocument();
    expect(within(dialog).getByText('JPEG, PNG or WebP, at most 5 MB.')).toBeInTheDocument();
    // The same nodes, the same sizes, the same values: nothing was remounted or reset.
    expect(within(dialog).getByTestId('product-image-field')).toBe(field);
    expect(imageIn(field)).toBe(preview);
    expect(within(dialog).getByTestId('product-image-input')).toBe(input);
    expect(buttonWidths()).toEqual(widths);
    expect(widths.every((width) => width !== '')).toBe(true);
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Trà đang sửa');
    expect(images.presigns).toEqual([]);

    await switchLanguage(user, 'VI');
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Tệp lớn hơn 5 MB.');
    expect(within(dialog).getByLabelText('Tên sản phẩm')).toHaveValue('Trà đang sửa');
  });
});
