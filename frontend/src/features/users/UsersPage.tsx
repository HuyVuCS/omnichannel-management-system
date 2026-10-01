import { KeyOutlined, LockOutlined, PlusOutlined, UnlockOutlined, UserSwitchOutlined } from '@ant-design/icons';
import { App as AntApp, Button, Input, Select, Space, Table, Tag, Tooltip, type TableProps } from 'antd';
import { useCallback, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ROLES, type Role } from '@/features/auth/session';
import { translateApiError } from '@/shared/api/problemDetails';
import { isApiError, isUnexpectedError } from '@/shared/api/types';
import { FILTER_WIDTH, PAGE_ACTION_WIDTH, SEARCH_WIDTH } from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { TruncatedText } from '@/shared/components/TruncatedText';
import { useListQueryState } from '@/shared/hooks/useListQueryState';
import { formatDateTime } from '@/shared/utils/format';
import { ChangeRoleModal } from './components/ChangeRoleModal';
import { CreateUserModal } from './components/CreateUserModal';
import { ResetPasswordModal } from './components/ResetPasswordModal';
import { RoleSelect } from './components/RoleSelect';
import { useListUsersQuery, useLockUserMutation, useUnlockUserMutation, type User } from './usersApi';

/** `GET /api/users` sorts by e-mail only. */
const SORT_FIELDS = ['email'] as const;

/** An admin lock is stored as a lockout that never ends (year 9999). */
const INDEFINITE_LOCK_YEAR = 9999;

type StatusFilter = 'locked' | 'active';
type Dialog = { kind: 'create' } | { kind: 'role'; user: User } | { kind: 'reset'; user: User } | null;

export function UsersPage() {
  const { t } = useTranslation();
  const { message, modal } = AntApp.useApp();
  const list = useListQueryState({ sortFields: SORT_FIELDS, defaultSortField: 'email' });
  const { searchParams, patchParams } = list;

  // Page-specific filters, in the query string like the rest of the list state.
  const roleParam = searchParams.get('role');
  const role = (ROLES as readonly string[]).includes(roleParam ?? '') ? (roleParam as Role) : undefined;
  const lockedParam = searchParams.get('locked');
  const locked = lockedParam === 'true' ? true : lockedParam === 'false' ? false : undefined;

  const { data, isFetching } = useListUsersQuery({
    page: list.page,
    pageSize: list.pageSize,
    search: list.search,
    sortDir: list.sortDir,
    role,
    locked,
  });
  const [lockUser] = useLockUserMutation();
  const [unlockUser] = useUnlockUserMutation();
  const [dialog, setDialog] = useState<Dialog>(null);

  const confirmLockChange = useCallback(
    (user: User) => {
      const locking = !user.isLocked;
      modal.confirm({
        title: t(locking ? 'users.confirm.lockTitle' : 'users.confirm.unlockTitle'),
        content: t(locking ? 'users.confirm.lockContent' : 'users.confirm.unlockContent', { email: user.email }),
        okText: t(locking ? 'users.actions.lock' : 'users.actions.unlock'),
        cancelText: t('common.cancel'),
        okButtonProps: { danger: locking },
        onOk: async () => {
          try {
            await (locking ? lockUser(user.userId) : unlockUser(user.userId)).unwrap();
            message.success(t(locking ? 'users.messages.locked' : 'users.messages.unlocked'));
          } catch (error) {
            // Network errors and 5xx are reported globally (with the correlation id).
            if (isApiError(error) && !isUnexpectedError(error)) {
              message.error(translateApiError(t, error));
            }
          }
        },
      });
    },
    [lockUser, message, modal, t, unlockUser],
  );

  const { sortOrder } = list;
  const columns = useMemo<TableProps<User>['columns']>(
    () => [
      {
        key: 'email',
        dataIndex: 'email',
        title: t('users.columns.email'),
        width: 320,
        ellipsis: { showTitle: false },
        sorter: true,
        sortOrder: sortOrder('email'),
        render: (email: string) => <TruncatedText text={email} />,
      },
      {
        key: 'role',
        dataIndex: 'role',
        title: t('users.columns.role'),
        width: 160,
        ellipsis: true,
        render: (userRole: Role | null) => <Tag>{userRole ? t(`roles.${userRole}`) : t('roles.none')}</Tag>,
      },
      {
        key: 'status',
        title: t('users.columns.status'),
        width: 160,
        ellipsis: true,
        render: (_: unknown, user) => {
          if (!user.isLocked) {
            return <Tag color="success">{t('users.status.active')}</Tag>;
          }

          const byAdmin =
            user.lockoutEnd !== null && new Date(user.lockoutEnd).getUTCFullYear() >= INDEFINITE_LOCK_YEAR;
          const reason = byAdmin
            ? t('users.status.lockedByAdmin')
            : t('users.status.lockedUntil', { time: formatDateTime(user.lockoutEnd) });
          return (
            <Tooltip title={reason}>
              <Tag color="error">{t('users.status.locked')}</Tag>
            </Tooltip>
          );
        },
      },
      {
        key: 'accessFailedCount',
        dataIndex: 'accessFailedCount',
        title: t('users.columns.failedAttempts'),
        width: 160,
        align: 'right',
        ellipsis: true,
      },
      {
        key: 'actions',
        title: t('users.columns.actions'),
        width: 152,
        ellipsis: true,
        // Icon buttons: the same size and position in both languages; the label is the tooltip.
        render: (_: unknown, user) => (
          <Space size={4}>
            <Tooltip title={t('users.actions.changeRole')}>
              <Button
                type="text"
                icon={<UserSwitchOutlined />}
                aria-label={t('users.actions.changeRole')}
                onClick={() => setDialog({ kind: 'role', user })}
              />
            </Tooltip>
            <Tooltip title={t(user.isLocked ? 'users.actions.unlock' : 'users.actions.lock')}>
              <Button
                type="text"
                icon={user.isLocked ? <UnlockOutlined /> : <LockOutlined />}
                aria-label={t(user.isLocked ? 'users.actions.unlock' : 'users.actions.lock')}
                onClick={() => confirmLockChange(user)}
              />
            </Tooltip>
            <Tooltip title={t('users.actions.resetPassword')}>
              <Button
                type="text"
                icon={<KeyOutlined />}
                aria-label={t('users.actions.resetPassword')}
                onClick={() => setDialog({ kind: 'reset', user })}
              />
            </Tooltip>
          </Space>
        ),
      },
    ],
    [confirmLockChange, sortOrder, t],
  );

  const statusFilter: StatusFilter | undefined = locked === undefined ? undefined : locked ? 'locked' : 'active';

  return (
    <>
      <PageHeader
        title={t('users.title')}
        extra={
          <Button
            type="primary"
            icon={<PlusOutlined />}
            onClick={() => setDialog({ kind: 'create' })}
            style={{ width: PAGE_ACTION_WIDTH }}
          >
            {t('users.create')}
          </Button>
        }
      />
      <Space wrap style={{ marginBottom: 16 }}>
        <Input.Search
          allowClear
          value={list.searchText}
          placeholder={t('users.searchPlaceholder')}
          aria-label={t('users.searchPlaceholder')}
          onChange={(event) => list.setSearchText(event.target.value)}
          style={{ width: SEARCH_WIDTH }}
        />
        <RoleSelect
          allowClear
          value={role}
          placeholder={t('users.filterRole')}
          aria-label={t('users.filterRole')}
          onChange={(value) => patchParams({ role: value, page: undefined })}
          style={{ width: FILTER_WIDTH }}
        />
        <Select<StatusFilter>
          allowClear
          value={statusFilter}
          placeholder={t('users.filterStatus')}
          aria-label={t('users.filterStatus')}
          options={[
            { value: 'active', label: t('users.status.active') },
            { value: 'locked', label: t('users.status.locked') },
          ]}
          onChange={(status) =>
            patchParams({ locked: status === undefined ? undefined : String(status === 'locked'), page: undefined })
          }
          style={{ width: FILTER_WIDTH }}
        />
      </Space>
      <Table<User>
        rowKey="userId"
        columns={columns}
        dataSource={data?.items}
        loading={isFetching}
        tableLayout="fixed"
        scroll={{ x: 952 }}
        sortDirections={['ascend', 'descend']}
        onChange={list.onTableChange}
        pagination={list.pagination(data?.totalCount)}
      />
      <CreateUserModal open={dialog?.kind === 'create'} onClose={() => setDialog(null)} />
      <ChangeRoleModal user={dialog?.kind === 'role' ? dialog.user : null} onClose={() => setDialog(null)} />
      <ResetPasswordModal user={dialog?.kind === 'reset' ? dialog.user : null} onClose={() => setDialog(null)} />
    </>
  );
}
