import { Segmented } from 'antd';
import { useTranslation } from 'react-i18next';
import { currentLanguage, isLanguage, type Language } from '@/shared/i18n';
import { LANGUAGE_SWITCH_WIDTH } from './layout';

// Language codes, the same in both languages: the control never changes size.
const OPTIONS: { label: string; value: Language }[] = [
  { label: 'VI', value: 'vi' },
  { label: 'EN', value: 'en' },
];

/**
 * Fixed-width `VI | EN` control. Changing it re-renders texts in place (i18next + AntD locale); it never reloads,
 * navigates or remounts anything.
 */
export function LanguageSwitch() {
  const { t, i18n } = useTranslation();

  return (
    <Segmented<Language>
      aria-label={t('layout.language')}
      options={OPTIONS}
      value={currentLanguage()}
      onChange={(language) => {
        if (isLanguage(language)) {
          void i18n.changeLanguage(language);
        }
      }}
      style={{ width: LANGUAGE_SWITCH_WIDTH, flex: 'none' }}
      block
    />
  );
}
