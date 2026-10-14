import { screen } from '@testing-library/react';
import { http } from 'msw';
import { describe, expect, it } from 'vitest';
import { CHANNELS_PATH } from '@/features/channels/paths';
import { STOCK_IMPORT_PATH } from '@/features/inventory/paths';
import { ORDERS_PATH } from '@/features/orders/paths';
import { PROCESSING_PATH } from '@/features/processing/paths';
import { settled } from '@/test/interactions';
import { renderApp } from '@/test/renderApp';
import { adminUser, api, problem, server, sessionOf, staffUser } from '@/test/server';
import { navigationItems } from './navigation';

const SOME_ID = '0199c0de-0000-7000-8000-000000000027';

/**
 * Every API request of these pages that the default handlers do not answer: "not found". A list page keeps its title
 * either way, and a detail page shows its not-found page - which has a title of its own.
 */
function answerEverythingNotFound() {
  server.use(
    http.all(api('/*'), ({ request }) => {
      const path = new URL(request.url).pathname;
      if (path === '/api/auth/me' || path === '/api/dashboard/summary') {
        return undefined;
      }
      return problem(404);
    }),
  );
}

async function expectOneHeadingOne() {
  await screen.findAllByRole('heading', { level: 1 });
  await settled();
  expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
}

/** Task 027 A4 (review 026c q3): every page has exactly one `<h1>`, its title. */
describe('one <h1> per page', () => {
  const signedIn = [
    ...navigationItems.map((item) => item.path),
    STOCK_IMPORT_PATH,
    `${ORDERS_PATH}/${SOME_ID}`,
    `${CHANNELS_PATH}/${SOME_ID}`,
    `${PROCESSING_PATH}/tasks/${SOME_ID}`,
    '/no-such-page',
  ];

  it.each(signedIn)('%s (Admin)', async (route) => {
    answerEverythingNotFound();
    renderApp({ route, session: sessionOf(adminUser) });

    await expectOneHeadingOne();
  });

  it('the forbidden page', async () => {
    answerEverythingNotFound();
    renderApp({ route: '/users', session: sessionOf(staffUser) });

    expect(await screen.findByRole('heading', { level: 1, name: 'Không có quyền truy cập' })).toBeInTheDocument();
    await expectOneHeadingOne();
  });

  it('the login page', async () => {
    renderApp({ route: '/login' });

    expect(await screen.findByRole('heading', { level: 1, name: 'Đăng nhập' })).toBeInTheDocument();
    await expectOneHeadingOne();
  });
});
