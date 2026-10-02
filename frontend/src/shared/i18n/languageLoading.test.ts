import { beforeEach, describe, expect, it, vi } from 'vitest';
import { LANGUAGE_STORAGE_KEY } from './index';

/**
 * Only the language in use is downloaded (task 016 A5). Every test gets its own instance of the i18n module - as a
 * page load does - so "not loaded yet" is a real starting point.
 */
async function freshI18n() {
  vi.resetModules();
  const module = await import('./index');
  return { ...module, i18n: module.default };
}

describe('language loading', () => {
  beforeEach(() => window.localStorage.clear());

  it('starts in Vietnamese with the Vietnamese texts only', async () => {
    const { i18n, loadActiveLanguage, currentLanguage } = await freshI18n();

    // Before the texts arrived a key is all there is: this is what `main.tsx` waits for.
    expect(i18n.t('auth.submit')).toBe('auth.submit');

    await loadActiveLanguage();

    expect(currentLanguage()).toBe('vi');
    expect(i18n.t('auth.submit')).toBe('Đăng nhập');
    expect(i18n.hasResourceBundle('vi', 'translation')).toBe(true);
    expect(i18n.hasResourceBundle('en', 'translation')).toBe(false);
  });

  it('starts in English with the English texts only when that is the stored choice', async () => {
    window.localStorage.setItem(LANGUAGE_STORAGE_KEY, 'en');
    const { i18n, loadActiveLanguage, currentLanguage } = await freshI18n();

    await loadActiveLanguage();

    expect(currentLanguage()).toBe('en');
    expect(i18n.t('auth.submit')).toBe('Sign in');
    expect(i18n.hasResourceBundle('en', 'translation')).toBe(true);
    expect(i18n.hasResourceBundle('vi', 'translation')).toBe(false);
  });

  it('loads the other language on the first switch, before the language changes, and only once', async () => {
    const { i18n, loadActiveLanguage, changeLanguage, languageLoaders, currentLanguage } = await freshI18n();
    await loadActiveLanguage();
    const loadEnglish = vi.spyOn(languageLoaders, 'en');
    const loadVietnamese = vi.spyOn(languageLoaders, 'vi');

    // What a component sees when it re-renders for the new language: the texts are there already.
    const seenAtChange: string[] = [];
    i18n.on('languageChanged', () => seenAtChange.push(i18n.t('auth.submit')));

    await changeLanguage('en');
    expect(currentLanguage()).toBe('en');
    expect(window.localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('en');

    await changeLanguage('vi');
    await changeLanguage('en');

    expect(seenAtChange).toEqual(['Sign in', 'Đăng nhập', 'Sign in']);
    expect(loadEnglish).toHaveBeenCalledTimes(1);
    expect(loadVietnamese).not.toHaveBeenCalled();
  });

  it('keeps the current language when the texts cannot be loaded, and tries again on the next switch', async () => {
    const { i18n, loadActiveLanguage, changeLanguage, languageLoaders, currentLanguage } = await freshI18n();
    await loadActiveLanguage();
    const loadEnglish = vi.spyOn(languageLoaders, 'en').mockRejectedValueOnce(new Error('chunk is gone'));

    await expect(changeLanguage('en')).rejects.toThrow('chunk is gone');

    expect(currentLanguage()).toBe('vi');
    expect(i18n.t('auth.submit')).toBe('Đăng nhập');
    expect(i18n.hasResourceBundle('en', 'translation')).toBe(false);
    expect(window.localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('vi');

    await changeLanguage('en');

    expect(currentLanguage()).toBe('en');
    expect(i18n.t('auth.submit')).toBe('Sign in');
    expect(loadEnglish).toHaveBeenCalledTimes(2);
  });

  it('lets the later of two overlapping switches win', async () => {
    const { loadActiveLanguage, changeLanguage, languageLoaders, currentLanguage } = await freshI18n();
    await loadActiveLanguage();
    const english = await import('./en.json');
    let finishLoading = () => {};
    vi.spyOn(languageLoaders, 'en').mockImplementationOnce(
      () => new Promise((resolve) => (finishLoading = () => resolve(english))),
    );

    // EN is still loading when the user goes back to VI.
    const toEnglish = changeLanguage('en');
    const toVietnamese = changeLanguage('vi');
    finishLoading();
    await Promise.all([toEnglish, toVietnamese]);

    expect(currentLanguage()).toBe('vi');
  });
});
