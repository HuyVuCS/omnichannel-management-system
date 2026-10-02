import { App as AntApp, Segmented } from 'antd';
import { useTranslation } from 'react-i18next';
import { changeLanguage, currentLanguage, isLanguage, type Language } from '@/shared/i18n';
import { LANGUAGE_SWITCH_WIDTH } from './layout';

// Language codes, the same in both languages: the control never changes size.
const OPTIONS: { label: string; value: Language }[] = [
  { label: 'VI', value: 'vi' },
  { label: 'EN', value: 'en' },
];

const NOTIFICATION_KEY = 'language-load';

/**
 * Fixed-width `VI | EN` control. Changing it re-renders texts in place (i18next + AntD locale); it never reloads,
 * navigates or remounts anything. The texts of a language are downloaded on the first switch to it, before the switch
 * happens; when that fails the language stays as it is and a notification says so.
 */
export function LanguageSwitch() {
  const { t } = useTranslation();
  const { notification } = AntApp.useApp();

  return (
    <Segmented<Language>
      aria-label={t('layout.language')}
      options={OPTIONS}
      value={currentLanguage()}
      onChange={(language) => {
        if (isLanguage(language)) {
          changeLanguage(language).then(
            () => notification.destroy(NOTIFICATION_KEY),
            () => notification.error({ key: NOTIFICATION_KEY, title: t('errors.languageLoadFailed') }),
          );
        }
      }}
      style={{ width: LANGUAGE_SWITCH_WIDTH, flex: 'none' }}
      block
    />
  );
}
