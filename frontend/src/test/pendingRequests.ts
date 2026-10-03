/**
 * A test must end with its requests answered. MSW takes the handlers of a request when the request reaches it and
 * answers when the handler is done; a request that is still on its way when a test ends is answered while the next
 * test runs - by handlers that were reset in between, or counted by that test. So the setup fails a test that ends
 * with a request that was sent and not yet answered.
 *
 * A request is counted from the `fetch` call until its promise settles. MSW's own life-cycle events
 * (`server.events`: `request:start` … `request:end`) cover less: MSW reports a request about one macrotask after the
 * call (measured: 19 ms), so a request sent in the last tick of a test would not be seen before the next test runs -
 * and would then be held against that one.
 */
const pending = new Map<number, string>();
let nextId = 0;
let allowedReason: string | undefined;

function requestLine(input: RequestInfo | URL, init: RequestInit | undefined): string {
  if (input instanceof Request) {
    return `${input.method} ${input.url}`;
  }

  return `${init?.method ?? 'GET'} ${input instanceof URL ? input.href : input}`;
}

/**
 * Starts counting. Call it after `server.listen()`: the wrapper sits on top of the `fetch` MSW has patched. Returns
 * the function that removes it again.
 */
export function trackPendingRequests(): () => void {
  const inner = globalThis.fetch;

  const tracked: typeof fetch = (input, init) => {
    const id = nextId++;
    pending.set(id, requestLine(input, init));
    // Either way the request is over: an answer (any status), a network error, or an abort.
    const settled = () => pending.delete(id);
    const answer = inner(input, init);
    answer.then(settled, settled);
    return answer;
  };

  globalThis.fetch = tracked;
  return () => {
    if (globalThis.fetch === tracked) {
      globalThis.fetch = inner;
    }
  };
}

/**
 * The one opt-out, for a test whose subject is a request that is never answered (a handler that waits forever, to look
 * at the screen while a request is on its way). Call it in that test with the reason; it holds for that test only.
 */
export function allowUnansweredRequests(reason: string): void {
  allowedReason = reason;
}

/** The requests that are on their way right now (`METHOD url`). */
export function unansweredRequests(): string[] {
  return [...pending.values()];
}

/**
 * The requests the test that just ended left unanswered (`METHOD url`), unless it opted out. Called by the setup after
 * each test; it also forgets them, so the next test starts clean.
 */
export function takeUnansweredRequests(): string[] {
  const left = allowedReason === undefined ? [...pending.values()] : [];
  pending.clear();
  allowedReason = undefined;
  return left;
}
