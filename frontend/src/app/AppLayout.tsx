import { LogoutOutlined } from '@ant-design/icons';
import { Button, Layout, Menu, Tag, theme, Tooltip, Typography } from 'antd';
import { Suspense, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, Outlet, useLocation } from 'react-router';
import { selectCurrentUser, signedOut } from '@/features/auth/authSlice';
import { hasAnyRole } from '@/features/auth/session';
import { LanguageSwitch } from '@/shared/components/LanguageSwitch';
import { PageLoading } from '@/shared/components/PageLoading';
import {
  HEADER_HEIGHT,
  ROLE_TAG_WIDTH,
  SIDER_COLLAPSED_WIDTH,
  SIDER_WIDTH,
  USER_BLOCK_WIDTH,
} from '@/shared/components/layout';
import { useAppDispatch, useAppSelector } from '@/shared/hooks/store';
import { activeNavigationPath, navigationItems } from './navigation';

const { Sider, Header, Content } = Layout;

/**
 * Shell of every signed-in page: collapsible menu (items filtered by role), header with the user, the language switch
 * and logout. All sizes are fixed constants, so the shell looks the same in both languages; long labels are truncated
 * (menu labels by AntD, the e-mail and role here) and show the full text as a tooltip.
 */
export function AppLayout() {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  const dispatch = useAppDispatch();
  const user = useAppSelector(selectCurrentUser);
  const location = useLocation();
  const [collapsed, setCollapsed] = useState(false);

  const menuItems = useMemo(
    () =>
      navigationItems
        .filter((item) => !item.roles || hasAnyRole(user, item.roles))
        .map((item) => ({
          key: item.path,
          icon: item.icon,
          title: t(item.labelKey),
          label: <Link to={item.path}>{t(item.labelKey)}</Link>,
        })),
    [t, user],
  );

  const activePath = activeNavigationPath(location.pathname);
  const role = user?.roles[0];
  const roleLabel = role ? t(`roles.${role}`, { defaultValue: role }) : t('roles.none');

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Sider
        data-testid="app-sider"
        theme="light"
        collapsible
        collapsed={collapsed}
        onCollapse={setCollapsed}
        breakpoint="lg"
        width={SIDER_WIDTH}
        collapsedWidth={SIDER_COLLAPSED_WIDTH}
        style={{ borderInlineEnd: `1px solid ${token.colorBorderSecondary}` }}
      >
        <div
          style={{
            height: HEADER_HEIGHT,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            padding: '0 16px',
            overflow: 'hidden',
          }}
        >
          <Typography.Text strong ellipsis style={{ color: token.colorPrimary, fontSize: 16 }}>
            {collapsed ? t('app.name').charAt(0) : t('app.name')}
          </Typography.Text>
        </div>
        <Menu
          aria-label={t('layout.menu')}
          mode="inline"
          selectedKeys={activePath ? [activePath] : []}
          items={menuItems}
          style={{ borderInlineEnd: 'none' }}
        />
      </Sider>
      <Layout>
        <Header
          data-testid="app-header"
          style={{
            height: HEADER_HEIGHT,
            lineHeight: `${HEADER_HEIGHT}px`,
            padding: '0 16px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'flex-end',
            gap: 12,
            background: token.colorBgContainer,
            borderBottom: `1px solid ${token.colorBorderSecondary}`,
          }}
        >
          <div
            data-testid="app-user"
            style={{
              width: USER_BLOCK_WIDTH,
              minWidth: 0,
              flex: '0 1 auto',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'flex-end',
              gap: 8,
            }}
          >
            <Typography.Text ellipsis={{ tooltip: user?.email }} style={{ minWidth: 0 }}>
              {user?.email}
            </Typography.Text>
            <Tag
              title={roleLabel}
              style={{
                width: ROLE_TAG_WIDTH,
                flex: 'none',
                marginInlineEnd: 0,
                textAlign: 'center',
                overflow: 'hidden',
                textOverflow: 'ellipsis',
              }}
            >
              {roleLabel}
            </Tag>
          </div>
          <LanguageSwitch />
          <Tooltip title={t('layout.logout')}>
            <Button
              type="text"
              icon={<LogoutOutlined />}
              aria-label={t('layout.logout')}
              onClick={() => dispatch(signedOut())}
            />
          </Tooltip>
        </Header>
        <Content style={{ padding: 24, minWidth: 0 }}>
          {/* A boundary per page: while the code of a page loads, the spinner fills the content area and the shell
              stays as it is. Keyed by path only, so query-string changes (list state) never remount the page. */}
          <Suspense key={location.pathname} fallback={<PageLoading />}>
            <Outlet />
          </Suspense>
        </Content>
      </Layout>
    </Layout>
  );
}
