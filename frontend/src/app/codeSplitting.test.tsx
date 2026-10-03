import { screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { renderApp } from '@/test/renderApp';
import { api, server, sessionOf, staffUser } from '@/test/server';
import { settled } from '@/test/interactions';

/**
 * Task 011: every page is its own chunk (`React.lazy`). The first time a route is opened its code is loaded while the
 * shell stays and a spinner fills the content area. Each page is opened once per test file here, so its module is not
 * loaded yet when the test starts.
 */
describe('route-level code splitting', () => {
  it('shows the shell and the loading fallback first, then the page', async () => {
    server.use(
      http.get(api('/customers'), () => HttpResponse.json({ items: [], page: 1, pageSize: 20, totalCount: 0 })),
    );
    renderApp({ route: '/customers', session: sessionOf(staffUser) });

    // Synchronously after the first render: the page's code is still loading.
    expect(screen.getByTestId('page-loading')).toBeInTheDocument();
    expect(screen.getByTestId('app-sider')).toBeInTheDocument();
    expect(screen.getByTestId('app-header')).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Khách hàng' })).not.toBeInTheDocument();

    expect(await screen.findByRole('heading', { name: 'Khách hàng' })).toBeInTheDocument();
    expect(screen.queryByTestId('page-loading')).not.toBeInTheDocument();

    await settled();
  });

  it('loads the chunk of a page when the user navigates to it through the menu', async () => {
    server.use(http.get(api('/channels'), () => HttpResponse.json([])));
    const { user, location } = renderApp({ route: '/reports', session: sessionOf(staffUser) });
    expect(await screen.findByText('Trang này sẽ có ở task 021.')).toBeInTheDocument();
    const sider = screen.getByTestId('app-sider');

    await user.click(screen.getByRole('link', { name: 'Kênh bán' }));

    // The fallback appears in the content area; the shell is the same node as before (not remounted).
    await waitFor(() => expect(location()).toBe('/channels'));
    expect(await screen.findByRole('heading', { name: 'Kênh bán' })).toBeInTheDocument();
    expect(screen.queryByTestId('page-loading')).not.toBeInTheDocument();
    expect(screen.getByTestId('app-sider')).toBe(sider);

    await settled();
  });

  it('shows the full-screen fallback while the login page loads', async () => {
    renderApp({ route: '/login' });

    expect(screen.getByTestId('page-loading')).toBeInTheDocument();
    expect(await screen.findByLabelText('E-mail')).toBeInTheDocument();
    expect(screen.queryByTestId('page-loading')).not.toBeInTheDocument();
  });
});
