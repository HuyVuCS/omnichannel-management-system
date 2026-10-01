import { KeyOutlined, LockOutlined, PlusOutlined, UnlockOutlined, UserSwitchOutlined } from '@ant-design/icons';
import { App as AntApp, Button, Input, Select, Space, Table, Tag, Tooltip, Typography, type TableProps } from 'antd';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';
import { ROLES, type Role } from '@/features/auth/session';
import { translateApiError } from '@/shared/api/problemDetails';
import { isApiError, isUnexpectedError } from '@/shared/api/types';
import { FILTER_WIDTH, PAGE_ACTION_WIDTH, SEARCH_WIDTH } from '@/shared/components/layout';
import { PageHeader } from '@/shared/components/PageHeader';
import { useDebouncedValue } from '@/shared/hooks/useDebouncedValue';
import { formatDateTime } from '@/shared/utils/format';
import { ChangeRoleModal } from './components/ChangeRoleModal';
import { CreateUserModal } from './components/CreateUserModal';
import { ResetPasswordModal } from './components/ResetPasswordModal';
import { RoleSelect } from './components/RoleSelect';
import {
  useListUsersQuery,
  useLockUserMutation,
  useUnlockUserMutation,
  type SortDirection,
  type User,
  type UserListArgs,
} from './usersApi';

const DEFAULT_PAGE_SIZE = 20;
const PAGE_SIZES = [10, 20, 50];
const SEARCH_DEBOUNCE_MS = 300;

/** An admin lock is stored as a lockout that never ends (year 9999). */
const INDEFINITE_LOCK_YEAR = 9999;

type StatusFilter = 'locked' | 'active';
type Dialog = { kind: 'create' } | { kind: 'role'; user: User } | { kind: 'reset'; user: User } | null;

function positiveInt(value: string | null, fallback: number): number {
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : fallback;
}

/** The list state lives in the query string: it survives reloads and language switches and can be shared as a link. */
function readListArgs(params: URLSearchParams): UserListArgs {
  const role = params.get('role');
  const locked = params.get('locked');
  const sortDir = params.get('sortDir');
  const pageSize = positiveInt(params.get('pageSize'), DEFAULT_PAGE_SIZE);

  return {
    page: positiveInt(params.get('page'), 1),
    pageSize: PAGE_SIZES.includes(pageSize) ? pageSize : DEFAULT_PAGE_SIZE,
    search: params.get('search')?.trim() || undefined,
    role: (ROLES as readonly string[]).includes(role ?? '') ? (role as Role) : undefined,
    locked: locked === 'true' ? true : locked === 'false' ? false : undefined,
    sortDir: sortDir === 'asc' || sortDir === 'desc' ? sortDir : undefined,
  };
}

export function UsersPage() {
  const { t } = useTranslation();
  const { message, modal } = AntApp.useApp();
  const [searchParams, setSearchParams] = useSearchParams();
  const listArgs = useMemo(() => readListArgs(searchParams), [searchParams]);
  const { data, isFetching } = useListUsersQuery(listArgs);
  const [lockUser] = useLockUserMutation();
  const [unlockUser] = useUnlockUserMutation();
  const [dialog, setDialog] = useState<Dialog>(null);
  const [searchText, setSearchText] = useState(listArgs.search ?? '');
  const debouncedSearch = useDebouncedValue(searchText.trim(), SEARCH_DEBOUNCE_MS);

  const patchParams = useCallback(
    (changes: Record<string, string | number | undefined>) => {
      setSearchParams(
        (previous) => {
          const next = new URLSearchParams(previous);
          for (const [key, value] of Object.entries(changes)) {
            if (value === undefined || value === '') {
              next.delete(key);
            } else {
              next.set(key, String(value));
            }
          }

          return next;
        },
        { replace: true },
      );
    },
    [setSearchParams],
  );

  // A new search term starts on the first page.
  useEffect(() => {
    if (debouncedSearch !== (listArgs.search ?? '')) {
      patchParams({ search: debouncedSearch, page: undefined });
    }
  }, [debouncedSearch, listArgs.search, patchParams]);

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

  const columns = useMemo<TableProps<User>['columns']>(
    () => [
      {
        key: 'email',
        dataIndex: 'email',
        title: t('users.columns.email'),
        width: 320,
        ellipsis: { showTitle: false },
        sorter: true,
        sortOrder: listArgs.sortDir === 'desc' ? 'descend' : listArgs.sortDir === 'asc' ? 'ascend' : null,
        render: (email: string) => (
          <Typography.Text ellipsis={{ tooltip: email }} style={{ maxWidth: '100%' }}>
            {email}
          </Typography.Text>
        ),
      },
      {
        key: 'role',
        dataIndex: 'role',
        title: t('users.columns.role'),
        width: 160,
        ellipsis: true,
        render: (role: Role | null) => <Tag>{role ? t(`roles.${role}`) : t('roles.none')}</Tag>,
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
    [confirmLockChange, listArgs.sortDir, t],
  );

  const onTableChange: TableProps<User>['onChange'] = (pagination, _filters, sorter, extra) => {
    const order = Array.isArray(sorter) ? sorter[0]?.order : sorter.order;
    const sortDir: SortDirection | undefined = order === 'descend' ? 'desc' : order === 'ascend' ? 'asc' : undefined;
    patchParams({
      // A new sort order starts on the first page.
      page: extra.action === 'paginate' && pagination.current !== 1 ? pagination.current : undefined,
      pageSize: pagination.pageSize === DEFAULT_PAGE_SIZE ? undefined : pagination.pageSize,
      sortDir,
    });
  };

  const statusFilter: StatusFilter | undefined =
    listArgs.locked === undefined ? undefined : listArgs.locked ? 'locked' : 'active';

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
          value={searchText}
          placeholder={t('users.searchPlaceholder')}
          aria-label={t('users.searchPlaceholder')}
          onChange={(event) => setSearchText(event.target.value)}
          style={{ width: SEARCH_WIDTH }}
        />
        <RoleSelect
          allowClear
          value={listArgs.role}
          placeholder={t('users.filterRole')}
          aria-label={t('users.filterRole')}
          onChange={(role) => patchParams({ role, page: undefined })}
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
        onChange={onTableChange}
        pagination={{
          current: listArgs.page,
          pageSize: listArgs.pageSize,
          total: data?.totalCount ?? 0,
          pageSizeOptions: PAGE_SIZES,
          showSizeChanger: true,
        }}
      />
      <CreateUserModal open={dialog?.kind === 'create'} onClose={() => setDialog(null)} />
      <ChangeRoleModal user={dialog?.kind === 'role' ? dialog.user : null} onClose={() => setDialog(null)} />
      <ResetPasswordModal user={dialog?.kind === 'reset' ? dialog.user : null} onClose={() => setDialog(null)} />
    </>
  );
}
