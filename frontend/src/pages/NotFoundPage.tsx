import { Button, Result } from 'antd';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

export function NotFoundPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();

  return (
    <Result
      status="404"
      title={t('pages.notFound.title')}
      subTitle={t('pages.notFound.description')}
      extra={
        <Button type="primary" onClick={() => void navigate('/')}>
          {t('common.backToDashboard')}
        </Button>
      }
    />
  );
}
