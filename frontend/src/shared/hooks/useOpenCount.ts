import { useState } from 'react';

/**
 * How often a dialog has been opened, for `ResetFormOnOpen`: the number grows by one each time `opening` becomes a
 * different truthy value.
 *
 * It is counted by the dialog component, which renders on every change of what it is open for - also for the closed
 * state between two openings. The dialog's content cannot count for itself: while the dialog closes its content is
 * memoised and may never render the closed state, so a dialog reopened for the same record looks unchanged from inside.
 *
 * @param opening What the dialog is open for (the edited record, a state object, or `true`); falsy while closed.
 */
export function useOpenCount(opening: unknown): number {
  const [previous, setPrevious] = useState(opening);
  const [count, setCount] = useState(opening ? 1 : 0);

  // Adjusted while rendering (not in an effect): the content rendered with this pass already gets the new number.
  if (previous !== opening) {
    setPrevious(opening);
    if (opening) {
      setCount(count + 1);
    }
  }

  return count;
}
