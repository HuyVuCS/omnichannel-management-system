import { App as AntApp, ConfigProvider } from 'antd';
import enUS from 'antd/locale/en_US';
import viVN from 'antd/locale/vi_VN';
import type { ReactNode } from 'react';
import { I18nextProvider, useTranslation } from 'react-i18next';
import { Provider as ReduxProvider } from 'react-redux';
import i18n, { currentLanguage, type Language } from '@/shared/i18n';
import { THEME_COLORS } from '@/shared/components/layout';
import type { AppStore } from './store';

const ANTD_LOCALES = { vi: viVN, en: enUS } satisfies Record<Language, typeof viVN>;

/**
 * AntD theme and locale. The locale follows the i18next language, so AntD's built-in texts (pagination, empty state,
 * date picker, validation defaults) switch together with ours — by re-rendering, never by remounting the tree.
 *
 * One text for every required field of every form, in our own words: "{label} là bắt buộc" / "{label} is required"
 * (AntD's own differs per locale and per kind of field). `${label}` is AntD's placeholder; a rule that says more than
 * "required" keeps its own `message`.
 */
function AntdProvider({ children }: { children: ReactNode }) {
  // Subscribes this component to language changes.
  const { t } = useTranslation();

  return (
    <ConfigProvider
      locale={ANTD_LOCALES[currentLanguage()]}
      theme={{ token: THEME_COLORS }}
      form={{ validateMessages: { required: t('common.requiredField') } }}
    >
      <AntApp>{children}</AntApp>
    </ConfigProvider>
  );
}

interface AppProvidersProps {
  store: AppStore;
  children: ReactNode;
}

export function AppProviders({ store, children }: AppProvidersProps) {
  return (
    <ReduxProvider store={store}>
      <I18nextProvider i18n={i18n}>
        <AntdProvider>{children}</AntdProvider>
      </I18nextProvider>
    </ReduxProvider>
  );
}
