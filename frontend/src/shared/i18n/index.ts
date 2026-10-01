import dayjs from 'dayjs';
import 'dayjs/locale/vi';
import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import en from './en.json';
import vi from './vi.json';

export const LANGUAGES = ['vi', 'en'] as const;
export type Language = (typeof LANGUAGES)[number];

export const DEFAULT_LANGUAGE: Language = 'vi';

/** localStorage key of the UI language. A display preference only: nothing security-relevant is stored there. */
export const LANGUAGE_STORAGE_KEY = 'omnichannel.language';

export function isLanguage(value: unknown): value is Language {
  return typeof value === 'string' && (LANGUAGES as readonly string[]).includes(value);
}

/** The stored choice, or the default when nothing valid is stored or storage is unavailable (private mode, blocked). */
export function readStoredLanguage(): Language {
  try {
    const stored = window.localStorage.getItem(LANGUAGE_STORAGE_KEY);
    return isLanguage(stored) ? stored : DEFAULT_LANGUAGE;
  } catch {
    return DEFAULT_LANGUAGE;
  }
}

function storeLanguage(language: Language): void {
  try {
    window.localStorage.setItem(LANGUAGE_STORAGE_KEY, language);
  } catch {
    // Storage unavailable: the choice lasts for this page load only.
  }
}

/** The current UI language (also sent to the API as Accept-Language). */
export function currentLanguage(): Language {
  return isLanguage(i18n.language) ? i18n.language : DEFAULT_LANGUAGE;
}

/** Everything outside React that follows the language: dayjs texts and the document language. */
function applyLanguage(language: Language): void {
  dayjs.locale(language);
  document.documentElement.lang = language;
}

// Resources are bundled, so initialisation is synchronous: the first render already has its texts.
void i18n.use(initReactI18next).init({
  resources: { vi: { translation: vi }, en: { translation: en } },
  lng: readStoredLanguage(),
  fallbackLng: DEFAULT_LANGUAGE,
  supportedLngs: LANGUAGES,
  interpolation: { escapeValue: false }, // React escapes
  initAsync: false,
});

applyLanguage(currentLanguage());

i18n.on('languageChanged', (language) => {
  if (isLanguage(language)) {
    storeLanguage(language);
    applyLanguage(language);
  }
});

export default i18n;
