import { Button, Result } from 'antd';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

/** 403: signed in, but the role does not allow this page. */
export function ForbiddenPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();

  return (
    <Result
      status="403"
      title={t('pages.forbidden.title')}
      subTitle={t('pages.forbidden.description')}
      extra={
        <Button type="primary" onClick={() => void navigate('/')}>
          {t('common.backToDashboard')}
        </Button>
      }
    />
  );
}
