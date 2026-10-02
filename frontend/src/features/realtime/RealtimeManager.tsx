import { useEffect, useRef } from 'react';
import { useStore } from 'react-redux';
import type { AppStore } from '@/app/store';
import { selectAccessToken } from '@/features/auth/authSlice';
import { useAppSelector } from '@/shared/hooks/store';
import type { HubFactory } from './hub';
import { startRealtime } from './realtimeConnection';

/**
 * Keeps one hub connection per session, mounted once next to `SessionManager`: started when there is a session,
 * stopped when it ends (logout, expiry, a 401), and replaced when the access token changes (a new login is a new
 * connection). Without a session nothing is loaded and nothing connects.
 */
export function RealtimeManager({ createHub }: { createHub: HubFactory }) {
  const store = useStore() as AppStore;
  const accessToken = useAppSelector(selectAccessToken);
  /** Resolves when the previous connection is closed: the next one starts after it, never next to it. */
  const previousStopped = useRef<Promise<void>>(Promise.resolve());

  useEffect(() => {
    if (accessToken === null) {
      return;
    }

    let cancelled = false;
    let stop: (() => Promise<void>) | undefined;
    // One tick later: an effect that is cleaned up at once (React StrictMode runs every effect twice) starts nothing.
    void Promise.resolve().then(() => {
      if (!cancelled) {
        stop = startRealtime(store, createHub, previousStopped.current);
      }
    });

    return () => {
      cancelled = true;
      if (stop) {
        previousStopped.current = stop();
      }
    };
  }, [accessToken, createHub, store]);

  return null;
}
