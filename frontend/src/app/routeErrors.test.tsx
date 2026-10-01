import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { browser } from '@/shared/utils/browser';
import { expectLanguageSwitchChangesTextOnly } from '@/test/interactions';
import { renderApp } from '@/test/renderApp';
import { api, server, sessionOf, staffUser } from '@/test/server';

// The chunk of this page cannot be loaded (as after a redeploy: the old hashed file is gone).
vi.mock('@/features/customers/CustomersPage', () => {
  throw new TypeError('Failed to fetch dynamically imported module: /assets/CustomersPage-old.js');
});

// This page loads, but breaks while rendering.
vi.mock('@/features/channels/ChannelsPage', () => ({
  ChannelsPage: () => {
    throw new Error('secret internal detail at ChannelsPage.tsx:42');
  },
}));

// The chunk of the login page cannot be loaded either.
vi.mock('@/features/auth/LoginPage', () => {
  throw new TypeError('Importing a module script failed.');
});

/** Task 012 Part A3: no route ends on React Router's developer error screen. */
describe('route error page', () => {
  beforeEach(() => {
    // React and React Router log the caught error; keep the test output readable.
    vi.spyOn(console, 'error').mockImplementation(() => {});
  });

  it('tells the user to reload when the code of a page cannot be loaded, and the menu still works', async () => {
    const reload = vi.spyOn(browser, 'reload').mockImplementation(() => {});
    server.use(
      http.get(api('/products'), () => HttpResponse.json({ items: [], page: 1, pageSize: 20, totalCount: 0 })),
    );
    const { user, location } = renderApp({ route: '/customers', session: sessionOf(staffUser) });

    expect(await screen.findByText('Không tải được trang')).toBeInTheDocument();
    expect(
      screen.getByText('Có thể ứng dụng vừa có phiên bản mới. Hãy tải lại trang để tiếp tục.'),
    ).toBeInTheDocument();
    // Only a reload helps here.
    expect(screen.queryByRole('button', { name: 'Về trang tổng quan' })).not.toBeInTheDocument();
    // Nothing of the error itself is shown.
    expect(document.body).not.toHaveTextContent(/dynamically imported|CustomersPage-old|TypeError/);
    // The shell is intact.
    expect(screen.getByTestId('app-sider')).toBeInTheDocument();
    expect(location()).toBe('/customers');

    await user.click(screen.getByRole('button', { name: /Tải lại trang/ }));
    expect(reload).toHaveBeenCalledTimes(1);

    // Another page still opens from the menu, and the error is gone there.
    await user.click(screen.getByRole('link', { name: 'Sản phẩm' }));
    expect(await screen.findByRole('heading', { name: 'Sản phẩm' })).toBeInTheDocument();
    expect(screen.queryByTestId('route-error')).not.toBeInTheDocument();
    expect(location()).toBe('/products');
  });

  it('shows a generic message, a way back and the reload when a page throws while rendering', async () => {
    const reload = vi.spyOn(browser, 'reload').mockImplementation(() => {});
    const { user, location } = renderApp({ route: '/channels', session: sessionOf(staffUser) });

    expect(await screen.findByText('Không hiển thị được trang')).toBeInTheDocument();
    expect(screen.queryByText('Không tải được trang')).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent(/secret internal detail|ChannelsPage\.tsx|Error:/);
    expect(screen.getByTestId('app-sider')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Tải lại trang/ }));
    expect(reload).toHaveBeenCalledTimes(1);

    // Fixed widths: the two buttons stand side by side and their texts differ per language.
    expect(screen.getByRole('button', { name: /Tải lại trang/ })).toHaveStyle({ width: '176px' });
    expect(screen.getByRole('button', { name: 'Về trang tổng quan' })).toHaveStyle({ width: '176px' });

    await user.click(screen.getByRole('button', { name: 'Về trang tổng quan' }));
    expect(await screen.findByRole('heading', { name: 'Tổng quan' })).toBeInTheDocument();
    expect(location()).toBe('/');
    expect(screen.queryByTestId('route-error')).not.toBeInTheDocument();
  });

  it('is localised, and a language switch changes its text only', async () => {
    const { user, location } = renderApp({ route: '/channels', session: sessionOf(staffUser) });
    await screen.findByText('Không hiển thị được trang');

    await expectLanguageSwitchChangesTextOnly(user, location, {
      vietnamese: ['Không hiển thị được trang', 'Về trang tổng quan', 'Tải lại trang'],
      english: ['This page could not be displayed', 'Back to dashboard', 'Reload'],
      requestCount: () => 0,
    });
  });

  it('fills the window on the login route, where there is no layout around the page', async () => {
    const reload = vi.spyOn(browser, 'reload').mockImplementation(() => {});
    const { user } = renderApp({ route: '/login' });

    expect(await screen.findByText('Không tải được trang')).toBeInTheDocument();
    expect(screen.getByTestId('route-error').getAttribute('style')).toContain('min-height: 100vh');
    expect(screen.queryByTestId('app-sider')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Về trang tổng quan' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Tải lại trang/ }));
    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
  });
});
