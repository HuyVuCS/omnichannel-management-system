import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { languageLoaders } from '@/shared/i18n';
import { switchLanguage } from '@/test/interactions';
import { renderApp } from '@/test/renderApp';

/**
 * Task 016 A5 in the application: the first switch to a language downloads its texts. English has not been loaded in
 * this file before the test starts (the setup loads Vietnamese only).
 */
describe('language switch', () => {
  it('says so when the texts cannot be loaded and keeps the language; the next switch loads them once', async () => {
    const loadEnglish = vi.spyOn(languageLoaders, 'en').mockRejectedValueOnce(new Error('chunk is gone'));
    const { user } = renderApp({ route: '/login' });
    const submit = await screen.findByRole('button', { name: 'Đăng nhập' });
    const password = screen.getByLabelText('Mật khẩu');
    await user.type(password, 'typed before the switch');

    await switchLanguage(user, 'EN');

    expect(await screen.findByText('Không tải được ngôn ngữ đã chọn. Vui lòng thử lại.')).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'VI' })).toBeChecked();
    expect(screen.getByRole('button', { name: 'Đăng nhập' })).toBe(submit);
    expect(document.documentElement.lang).toBe('vi');

    // The second try works: text only - the same nodes, the typed value stays - and the notification goes away.
    await switchLanguage(user, 'EN');

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBe(submit);
    expect(screen.getByRole('radio', { name: 'EN' })).toBeChecked();
    expect(screen.getByLabelText('Password')).toBe(password);
    expect(password).toHaveValue('typed before the switch');
    await waitFor(() =>
      expect(screen.queryByText('Không tải được ngôn ngữ đã chọn. Vui lòng thử lại.')).not.toBeInTheDocument(),
    );

    // Loaded once: switching back and forth asks for nothing more.
    await switchLanguage(user, 'VI');
    expect(await screen.findByRole('button', { name: 'Đăng nhập' })).toBe(submit);
    await switchLanguage(user, 'EN');
    expect(await screen.findByRole('button', { name: 'Sign in' })).toBe(submit);
    expect(loadEnglish).toHaveBeenCalledTimes(2);
  });
});
