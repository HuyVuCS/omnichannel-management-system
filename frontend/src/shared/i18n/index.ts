import dayjs from 'dayjs';
import 'dayjs/locale/vi';
import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';

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

type Translations = Record<string, unknown>;

/**
 * One chunk per language, outside the first-load set: only the language in use is downloaded. An object (not a
 * `switch`) so a test can replace a loader.
 */
export const languageLoaders: Record<Language, () => Promise<{ default: Translations }>> = {
  vi: () => import('./vi.json'),
  en: () => import('./en.json'),
};

const NAMESPACE = 'translation';
const loading = new Map<Language, Promise<void>>();

/**
 * Makes the texts of a language available (once: later calls return the same promise). A failed load is forgotten, so
 * the next call tries again.
 */
export function loadLanguage(language: Language): Promise<void> {
  let pending = loading.get(language);
  if (!pending) {
    pending = languageLoaders[language]().then(
      (module) => {
        i18n.addResourceBundle(language, NAMESPACE, module.default, true, true);
      },
      (error: unknown) => {
        loading.delete(language);
        throw error;
      },
    );
    loading.set(language, pending);
  }

  return pending;
}

// No resources yet: initialisation stays synchronous, and `loadActiveLanguage` adds the texts of the language in use
// before the application renders (`main.tsx`), so the first render already has its texts.
void i18n.use(initReactI18next).init({
  resources: {},
  lng: readStoredLanguage(),
  fallbackLng: DEFAULT_LANGUAGE,
  supportedLngs: LANGUAGES,
  interpolation: { escapeValue: false }, // React escapes
  initAsync: false,
});

/** The texts of the language the application starts in. Awaited before the first render. */
export function loadActiveLanguage(): Promise<void> {
  return loadLanguage(currentLanguage());
}

let requestedLanguage: Language | undefined;

/**
 * Switches the UI language: the texts are loaded first (on the first switch to that language), then i18next changes
 * the language - so components never render with a language whose texts are missing. A failed load rejects and leaves
 * the current language as it is. Of two switches that overlap, the later one wins.
 */
export async function changeLanguage(language: Language): Promise<void> {
  requestedLanguage = language;
  await loadLanguage(language);
  if (requestedLanguage === language) {
    await i18n.changeLanguage(language);
  }
}

applyLanguage(currentLanguage());

i18n.on('languageChanged', (language) => {
  if (isLanguage(language)) {
    storeLanguage(language);
    applyLanguage(language);
  }
});

export default i18n;
