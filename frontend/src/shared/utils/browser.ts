/** Browser actions behind one object, so components do not call `window.location` directly (and tests can observe them). */
export const browser = {
  /** Reloads the application from the server. */
  reload(): void {
    window.location.reload();
  },
};
