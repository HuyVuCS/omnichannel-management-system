import { ReloadOutlined } from '@ant-design/icons';
import { Button, Result } from 'antd';
import { useTranslation } from 'react-i18next';
import { useNavigate, useRouteError } from 'react-router';
import { PageLoadError } from '@/app/lazyPage';
import { PAGE_ACTION_WIDTH } from '@/shared/components/layout';
import { ResultTitle } from '@/shared/components/ResultTitle';
import { browser } from '@/shared/utils/browser';

interface RouteErrorPageProps {
  /**
   * For routes outside the layout (login) and for errors of the shell itself: fills the window and offers the reload
   * only. Inside the layout the menu stays usable and the page also leads back to the dashboard.
   */
  fullScreen?: boolean;
}

/**
 * `errorElement` of the routes: shown instead of a page that could not be loaded or that threw while rendering. The
 * error itself is never shown (no message, no stack trace); React Router logs it to the console.
 */
export function RouteErrorPage({ fullScreen = false }: RouteErrorPageProps) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const outdated = useRouteError() instanceof PageLoadError;

  const reload = (
    <Button
      key="reload"
      type="primary"
      icon={<ReloadOutlined />}
      onClick={() => browser.reload()}
      style={{ width: PAGE_ACTION_WIDTH }}
    >
      {t('pages.routeError.reload')}
    </Button>
  );
  const toDashboard = (
    <Button key="dashboard" onClick={() => void navigate('/')} style={{ width: PAGE_ACTION_WIDTH }}>
      {t('common.backToDashboard')}
    </Button>
  );

  return (
    <div
      data-testid="route-error"
      style={
        fullScreen
          ? { minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16 }
          : undefined
      }
    >
      <Result
        status={outdated ? 'info' : 'error'}
        title={<ResultTitle>{t(outdated ? 'pages.routeError.outdatedTitle' : 'pages.routeError.title')}</ResultTitle>}
        subTitle={t(outdated ? 'pages.routeError.outdatedDescription' : 'pages.routeError.description')}
        extra={outdated || fullScreen ? [reload] : [toDashboard, reload]}
      />
    </div>
  );
}
