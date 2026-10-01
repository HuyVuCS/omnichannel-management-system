import { ClockCircleOutlined } from '@ant-design/icons';
import { Result } from 'antd';
import { useTranslation } from 'react-i18next';
import { PageHeader } from './PageHeader';

interface ComingSoonProps {
  /** Translation key of the page title (the menu label). */
  titleKey: string;
  /** Roadmap task that builds the page, e.g. `011`. */
  task: string;
}

/** Placeholder for a page of the menu that a later task builds. */
export function ComingSoon({ titleKey, task }: ComingSoonProps) {
  const { t } = useTranslation();

  return (
    <>
      <PageHeader title={t(titleKey)} />
      <Result
        icon={<ClockCircleOutlined />}
        title={t('comingSoon.title')}
        subTitle={t('comingSoon.description', { task })}
      />
    </>
  );
}
