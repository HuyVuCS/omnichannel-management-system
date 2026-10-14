import { Button, Result } from 'antd';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { ResultTitle } from '@/shared/components/ResultTitle';

/** 403: signed in, but the role does not allow this page. */
export function ForbiddenPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();

  return (
    <Result
      status="403"
      title={<ResultTitle>{t('pages.forbidden.title')}</ResultTitle>}
      subTitle={t('pages.forbidden.description')}
      extra={
        <Button type="primary" onClick={() => void navigate('/')}>
          {t('common.backToDashboard')}
        </Button>
      }
    />
  );
}
