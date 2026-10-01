import { baseApi } from '@/shared/api/baseApi';
import type { AuthUser } from './session';

export interface LoginRequest {
  email: string;
  password: string;
}

export interface LoginResponse {
  accessToken: string;
  tokenType: string;
  expiresAt: string;
  user: AuthUser;
}

export const authApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    login: build.mutation<LoginResponse, LoginRequest>({
      query: (body) => ({ url: 'auth/login', method: 'POST', body }),
    }),
    /** The user and roles as stored now; 401 when the token was revoked (role change, lock, password reset). */
    me: build.query<AuthUser, void>({
      query: () => 'auth/me',
    }),
  }),
});

export const { useLoginMutation } = authApi;
