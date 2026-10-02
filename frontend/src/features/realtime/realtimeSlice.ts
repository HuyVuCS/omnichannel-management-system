import { createSlice, type PayloadAction } from '@reduxjs/toolkit';
import { loggedOut, signedOut } from '@/features/auth/authSlice';

/**
 * - `connecting`: the first connection of this session is being established;
 * - `connected`: hints arrive, the screen is live;
 * - `reconnecting`: the connection was lost and is being re-established;
 * - `disconnected`: no session, or the quick retries are used up - the connection is still retried every 30 seconds.
 */
export type ConnectionState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

export interface RealtimeState {
  /** State of the hub connection. UI state only: what it delivers goes straight into RTK Query's cache as refetches. */
  connection: ConnectionState;
}

export interface StateWithRealtime {
  realtime: RealtimeState;
}

const initialState: RealtimeState = { connection: 'disconnected' };

const realtimeSlice = createSlice({
  name: 'realtime',
  initialState,
  reducers: {
    connectionChanged(state, action: PayloadAction<ConnectionState>) {
      state.connection = action.payload;
    },
  },
  extraReducers: (builder) => {
    // The connection belongs to the session and ends with it.
    builder.addCase(loggedOut, () => initialState).addCase(signedOut, () => initialState);
  },
});

export const { connectionChanged } = realtimeSlice.actions;
export const realtimeReducer = realtimeSlice.reducer;

export const selectConnectionState = (state: StateWithRealtime): ConnectionState => state.realtime.connection;
export const selectIsLive = (state: StateWithRealtime): boolean => state.realtime.connection === 'connected';
