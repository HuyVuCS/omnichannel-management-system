import { createSlice, type PayloadAction } from '@reduxjs/toolkit';
import type { AuthUser, Session } from './session';

export interface AuthState {
  /** Null = signed out. Client state only: everything else about the user comes from the API. */
  session: Session | null;
  /**
   * True after the user pressed "log out": the login page then opens without a return address (the next person at this
   * browser may be someone else). False when the session ended by itself (expired, revoked), where the user comes back
   * to the page they were on.
   */
  signedOutByUser: boolean;
}

/** The part of the store this slice needs; lets the API layer read the token without importing the store type. */
export interface StateWithAuth {
  auth: AuthState;
}

const initialState: AuthState = { session: null, signedOutByUser: false };

const authSlice = createSlice({
  name: 'auth',
  initialState,
  reducers: {
    /** After a successful login. */
    sessionStarted(state, action: PayloadAction<Session>) {
      state.session = action.payload;
      state.signedOutByUser = false;
    },
    /** `GET /api/auth/me`: e-mail and roles as the store has them now (the token keeps its own claims). */
    userRefreshed(state, action: PayloadAction<AuthUser>) {
      if (state.session) {
        state.session.user = action.payload;
      }
    },
    /** The session ended by itself: the token expired, or the API answered 401 (revoked). */
    loggedOut(state) {
      state.session = null;
      state.signedOutByUser = false;
    },
    /** The logout button. */
    signedOut(state) {
      state.session = null;
      state.signedOutByUser = true;
    },
  },
});

export const { sessionStarted, userRefreshed, loggedOut, signedOut } = authSlice.actions;
export const authReducer = authSlice.reducer;

export const selectSession = (state: StateWithAuth): Session | null => state.auth.session;
export const selectCurrentUser = (state: StateWithAuth): AuthUser | null => state.auth.session?.user ?? null;
export const selectSignedOutByUser = (state: StateWithAuth): boolean => state.auth.signedOutByUser;
export const selectAccessToken = (state: StateWithAuth): string | null => state.auth.session?.accessToken ?? null;
