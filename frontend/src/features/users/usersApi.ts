import type { Role } from '@/features/auth/session';
import { baseApi } from '@/shared/api/baseApi';
import type { PagedResult } from '@/shared/api/types';
import type { SortDirection } from '@/shared/hooks/useListQueryState';

export interface User {
  userId: string;
  email: string;
  /** Null only for a user without any role (cannot be created through the API). */
  role: Role | null;
  isLocked: boolean;
  /** ISO 8601; a date in the year 9999 means "until an administrator unlocks the user". */
  lockoutEnd: string | null;
  accessFailedCount: number;
}

/** Query string of `GET /api/users` (sorting is by e-mail only). */
export interface UserListArgs {
  page: number;
  pageSize: number;
  search?: string;
  role?: Role;
  locked?: boolean;
  sortDir?: SortDirection;
}

export interface CreateUserRequest {
  email: string;
  password: string;
  role: Role;
}

const LIST_TAG = { type: 'User', id: 'LIST' } as const;

/** Invalidate the list only when the mutation succeeded: a rejected one changed nothing on the server. */
const listOnSuccess = (_result: unknown, error: unknown) => (error ? [] : [LIST_TAG]);

/** Drops undefined values so they do not end up in the query string as the text "undefined". */
function compact(params: Record<string, string | number | boolean | undefined>) {
  return Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined && value !== ''));
}

export const usersApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    listUsers: build.query<PagedResult<User>, UserListArgs>({
      query: ({ page, pageSize, search, role, locked, sortDir }) => ({
        url: 'users',
        params: compact({ page, pageSize, search, role, locked, sortBy: sortDir ? 'email' : undefined, sortDir }),
      }),
      providesTags: (result) => [
        LIST_TAG,
        ...(result?.items.map((user) => ({ type: 'User' as const, id: user.userId })) ?? []),
      ],
    }),
    createUser: build.mutation<User, CreateUserRequest>({
      query: (body) => ({ url: 'users', method: 'POST', body }),
      invalidatesTags: listOnSuccess,
    }),
    // Role, lock and unlock change filter results and order as well, so the whole list is refetched.
    changeUserRole: build.mutation<User, { userId: string; role: Role }>({
      query: ({ userId, role }) => ({ url: `users/${userId}/role`, method: 'PUT', body: { role } }),
      invalidatesTags: listOnSuccess,
    }),
    lockUser: build.mutation<User, string>({
      query: (userId) => ({ url: `users/${userId}/lock`, method: 'POST' }),
      invalidatesTags: listOnSuccess,
    }),
    unlockUser: build.mutation<User, string>({
      query: (userId) => ({ url: `users/${userId}/unlock`, method: 'POST' }),
      invalidatesTags: listOnSuccess,
    }),
    resetUserPassword: build.mutation<User, { userId: string; newPassword: string }>({
      query: ({ userId, newPassword }) => ({
        url: `users/${userId}/reset-password`,
        method: 'POST',
        body: { newPassword },
      }),
      invalidatesTags: (_result, error, { userId }) => (error ? [] : [{ type: 'User', id: userId }]),
    }),
  }),
});

export const {
  useListUsersQuery,
  useCreateUserMutation,
  useChangeUserRoleMutation,
  useLockUserMutation,
  useUnlockUserMutation,
  useResetUserPasswordMutation,
} = usersApi;
