import { Button, Result } from 'antd';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { ResultTitle } from '@/shared/components/ResultTitle';

export function NotFoundPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();

  return (
    <Result
      status="404"
      title={<ResultTitle>{t('pages.notFound.title')}</ResultTitle>}
      subTitle={t('pages.notFound.description')}
      extra={
        <Button type="primary" onClick={() => void navigate('/')}>
          {t('common.backToDashboard')}
        </Button>
      }
    />
  );
}
