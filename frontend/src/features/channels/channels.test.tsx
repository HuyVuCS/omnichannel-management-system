import { screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import type { AuthUser } from '@/features/auth/session';
import { chooseOption, columnTexts, expectLanguageSwitchChangesTextOnly, findDialog, rowOf } from '@/test/interactions';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, managerUser, problem, server, sessionOf, staffUser } from '@/test/server';
import type { Channel } from './channelsApi';

function channel(overrides: Partial<Channel> & Pick<Channel, 'channelId' | 'name' | 'slug' | 'type'>): Channel {
  return { status: 'Active', createdAt: '2026-09-25T03:15:00+00:00', updatedAt: null, ...overrides };
}

const website = channel({ channelId: 'ch-web', name: 'Website', slug: 'website', type: 'Website' });
const pos = channel({ channelId: 'ch-pos', name: 'Cửa hàng', slug: 'pos', type: 'Pos', status: 'Inactive' });
const platform = channel({
  channelId: 'ch-dp',
  name: 'Sàn Shopee',
  slug: 'digital-platform',
  type: 'DigitalPlatform',
});

/** `GET /api/channels` (not paged) backed by a list the test can change; counts the requests. */
function serveChannels(initial: Channel[]) {
  const state = { channels: initial, requests: 0 };
  server.use(
    http.get(api('/channels'), () => {
      state.requests += 1;
      return HttpResponse.json(state.channels);
    }),
  );
  return state;
}

function renderChannels(user: AuthUser = adminUser) {
  return renderApp({ route: '/channels', session: sessionOf(user) });
}

describe('channels page', () => {
  it('lists every channel with slug, type and status', async () => {
    const state = serveChannels([website, pos, platform]);
    renderChannels();

    expect(await screen.findByText('digital-platform')).toBeInTheDocument();
    expect(columnTexts('Tên kênh')).toEqual(['Website', 'Cửa hàng', 'Sàn Shopee']);
    expect(columnTexts('Slug')).toEqual(['website', 'pos', 'digital-platform']);
    expect(columnTexts('Loại kênh')).toEqual(['Website', 'Tại quầy (POS)', 'Sàn thương mại điện tử']);
    expect(columnTexts('Trạng thái')).toEqual(['Đang hoạt động', 'Ngừng hoạt động', 'Đang hoạt động']);
    // Not paged.
    expect(document.querySelector('.ant-pagination')).toBeNull();
    expect(state.requests).toBe(1);
  });

  it('creates a channel (Admin) and refreshes the list', async () => {
    const state = serveChannels([website]);
    const bodies: unknown[] = [];
    server.use(
      http.post(api('/channels'), async ({ request }) => {
        bodies.push(await request.json());
        state.channels = [website, platform];
        return HttpResponse.json(platform, { status: 201 });
      }),
    );
    const { user: appUser } = renderChannels();

    await appUser.click(await screen.findByRole('button', { name: /Thêm kênh bán/ }));
    const dialog = await findDialog('Thêm kênh bán');
    await appUser.type(within(dialog).getByLabelText('Tên kênh'), 'Sàn Shopee');
    await appUser.type(within(dialog).getByLabelText('Slug'), 'digital-platform');
    expect(within(dialog).getByText(/ví dụ: san-thuong-mai/)).toBeInTheDocument();
    await chooseOption(appUser, within(dialog).getByRole('combobox', { name: 'Loại kênh' }), 'Sàn thương mại điện tử');
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    expect(await screen.findByText('Đã thêm kênh bán.')).toBeInTheDocument();
    expect(bodies).toEqual([{ name: 'Sàn Shopee', slug: 'digital-platform', type: 'DigitalPlatform' }]);
    expect(await screen.findByText('digital-platform')).toBeInTheDocument();
    expect(state.requests).toBe(2);
  });

  it('puts a duplicate slug (409) and the slug format error on the slug field, without refreshing the list', async () => {
    const state = serveChannels([website]);
    let conflict = true;
    server.use(
      http.post(api('/channels'), () =>
        conflict
          ? problem(409, { errorCode: 'channel_slug_exists' })
          : problem(400, {
              errorCode: 'validation_failed',
              errors: { slug: ["'slug' chỉ gồm chữ thường và chữ số, các từ cách nhau bằng một dấu gạch nối."] },
            }),
      ),
    );
    const { user: appUser } = renderChannels();

    await appUser.click(await screen.findByRole('button', { name: /Thêm kênh bán/ }));
    const dialog = await findDialog('Thêm kênh bán');
    await appUser.type(within(dialog).getByLabelText('Tên kênh'), 'Trùng');
    await appUser.type(within(dialog).getByLabelText('Slug'), 'website');
    await chooseOption(appUser, within(dialog).getByRole('combobox', { name: 'Loại kênh' }), 'Website');
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));

    const duplicate = await within(dialog).findByText('Đã có kênh bán với slug này.');
    expect(duplicate.closest('.ant-form-item')).toContainElement(within(dialog).getByLabelText('Slug'));

    conflict = false;
    await appUser.click(within(dialog).getByRole('button', { name: 'Tạo' }));
    const format = await within(dialog).findByText(/chỉ gồm chữ thường và chữ số/);
    expect(format.closest('.ant-form-item')).toContainElement(within(dialog).getByLabelText('Slug'));
    // The error of the previous attempt is replaced, not added to.
    expect(within(dialog).queryByText('Đã có kênh bán với slug này.')).not.toBeInTheDocument();
    expect(state.requests).toBe(1);
  });

  it('edits name and status; slug and type are read-only; deactivating needs a confirmation', async () => {
    const state = serveChannels([website, pos]);
    const updates: { id: string; body: unknown }[] = [];
    server.use(
      http.put(api('/channels/:id'), async ({ request, params }) => {
        updates.push({ id: String(params.id), body: await request.json() });
        const updated: Channel = { ...website, name: 'Website mới', status: 'Inactive' };
        state.channels = [updated, pos];
        return HttpResponse.json(updated);
      }),
    );
    const { user: appUser } = renderChannels();
    await screen.findByText('website');

    await appUser.click(within(rowOf('website')).getByRole('button', { name: 'Sửa' }));
    const dialog = await findDialog('Sửa kênh bán');
    expect(within(dialog).getByLabelText('Slug')).toBeDisabled();
    expect(within(dialog).getByLabelText('Slug')).toHaveValue('website');
    expect(within(dialog).getByRole('combobox', { name: 'Loại kênh' })).toBeDisabled();

    const name = within(dialog).getByLabelText('Tên kênh');
    await appUser.clear(name);
    await appUser.type(name, 'Website mới');
    await chooseOption(appUser, within(dialog).getByRole('combobox', { name: 'Trạng thái' }), 'Ngừng hoạt động');
    await appUser.click(within(dialog).getByRole('button', { name: 'Lưu' }));

    // Nothing is sent before the consequence is confirmed.
    const confirm = await findDialog('Ngừng kênh bán này?');
    expect(confirm).toHaveTextContent('Đơn hàng gửi tới kênh Website sẽ bị từ chối cho đến khi kênh hoạt động lại.');
    expect(updates).toEqual([]);

    await appUser.click(within(confirm).getByRole('button', { name: 'Huỷ' }));
    await waitFor(() => expect(screen.queryByText('Ngừng kênh bán này?')).not.toBeInTheDocument());
    expect(updates).toEqual([]);

    await appUser.click(within(dialog).getByRole('button', { name: 'Lưu' }));
    await appUser.click(within(await findDialog('Ngừng kênh bán này?')).getByRole('button', { name: 'Ngừng kênh' }));

    expect(await screen.findByText('Đã cập nhật kênh bán.')).toBeInTheDocument();
    expect(updates).toEqual([{ id: 'ch-web', body: { name: 'Website mới', status: 'Inactive' } }]);
    await waitFor(() => expect(columnTexts('Trạng thái')).toEqual(['Ngừng hoạt động', 'Ngừng hoạt động']));
    expect(state.requests).toBe(2);
  });

  it('activating a channel or renaming it needs no confirmation', async () => {
    const state = serveChannels([pos]);
    const updates: unknown[] = [];
    server.use(
      http.put(api('/channels/:id'), async ({ request }) => {
        updates.push(await request.json());
        const updated: Channel = { ...pos, status: 'Active' };
        state.channels = [updated];
        return HttpResponse.json(updated);
      }),
    );
    const { user: appUser } = renderChannels();
    await screen.findByText('pos');

    await appUser.click(within(rowOf('pos')).getByRole('button', { name: 'Sửa' }));
    const dialog = await findDialog('Sửa kênh bán');
    await chooseOption(appUser, within(dialog).getByRole('combobox', { name: 'Trạng thái' }), 'Đang hoạt động');
    await appUser.click(within(dialog).getByRole('button', { name: 'Lưu' }));

    expect(await screen.findByText('Đã cập nhật kênh bán.')).toBeInTheDocument();
    expect(updates).toEqual([{ name: 'Cửa hàng', status: 'Active' }]);
    expect(screen.queryByText('Ngừng kênh bán này?')).not.toBeInTheDocument();
  });
});

describe('channels page: roles', () => {
  it.each([
    ['Staff', staffUser, false],
    ['Manager', managerUser, false],
    ['Admin', adminUser, true],
  ] as const)('%s sees create and edit: %s', async (_role, user, canWrite) => {
    serveChannels([website, pos]);
    renderChannels(user);

    await screen.findByText('website');
    expect(screen.queryAllByRole('button', { name: /Thêm kênh bán/ })).toHaveLength(canWrite ? 1 : 0);
    expect(screen.queryAllByRole('button', { name: 'Sửa' })).toHaveLength(canWrite ? 2 : 0);
    expect(columnTexts('Slug')).toEqual(['website', 'pos']);
  });
});

describe('channels page: language switch', () => {
  it('changes text only: the list, the open form and its values stay', async () => {
    const state = serveChannels([website, pos, platform]);
    const { user: appUser, location } = renderChannels();
    await screen.findByText('digital-platform');

    await appUser.click(screen.getByRole('button', { name: /Thêm kênh bán/ }));
    const dialog = await findDialog('Thêm kênh bán');
    await appUser.type(within(dialog).getByLabelText('Tên kênh'), 'Đang nhập');
    await appUser.type(within(dialog).getByLabelText('Slug'), 'dang-nhap');

    await expectLanguageSwitchChangesTextOnly(appUser, location, {
      vietnamese: ['Tên kênh', 'Tại quầy (POS)', 'Ngừng hoạt động', 'Thêm kênh bán'],
      english: ['Name', 'Point of sale', 'Inactive', 'New channel'],
      requestCount: () => state.requests,
    });
    expect(location()).toBe('/channels');
  });
});

describe('channels page: dialog reopened before its close animation has finished', () => {
  it('shows the channel it was opened for, and a create dialog starts empty with slug and type editable', async () => {
    serveChannels([website, pos]);
    // Animations on: in jsdom they never finish, so the closed dialog and its form stay mounted.
    const { user: appUser } = renderApp({ route: '/channels', session: sessionOf(adminUser), animations: true });
    await screen.findByText('website');

    await appUser.click(within(rowOf('website')).getByRole('button', { name: 'Sửa' }));
    let dialog = await findDialog('Sửa kênh bán');
    await waitFor(() => expect(within(dialog).getByLabelText('Slug')).toHaveValue('website'));
    await appUser.click(within(dialog).getByRole('button', { name: 'Huỷ' }));

    await appUser.click(within(rowOf('pos')).getByRole('button', { name: 'Sửa' }));
    dialog = await findDialog('Sửa kênh bán');
    await waitFor(() => expect(within(dialog).getByLabelText('Slug')).toHaveValue('pos'));
    expect(within(dialog).getByLabelText('Tên kênh')).toHaveValue('Cửa hàng');
    expect(within(dialog).getByText('Ngừng hoạt động')).toBeInTheDocument(); // its status, not the previous one's
    await appUser.click(within(dialog).getByRole('button', { name: 'Huỷ' }));

    await appUser.click(screen.getByRole('button', { name: /Thêm kênh bán/ }));
    dialog = await findDialog('Thêm kênh bán');
    await waitFor(() => expect(within(dialog).getByLabelText('Slug')).toHaveValue(''));
    expect(within(dialog).getByLabelText('Slug')).toBeEnabled();
    expect(within(dialog).getByLabelText('Tên kênh')).toHaveValue('');
    expect(within(dialog).getByRole('combobox', { name: 'Loại kênh' })).toBeEnabled();
  });
});
