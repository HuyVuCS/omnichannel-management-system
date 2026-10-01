import { useEffect, useState } from 'react';

const COMMIT_DELAY_MS = 300;

/**
 * A text box bound to a value that lives in the address (a search term, a text filter).
 *
 * - Typing is local; `commit` writes the trimmed text to the address after a pause (one request per pause, not per key).
 * - When the value in the address changes and this hook did not write it (a menu link to the same page, back/forward,
 *   a "clear filters" button, an edited address), the box follows the address - and the old text is never written back.
 *
 * @param committed The value currently in the address (`''` when absent).
 * @param commit    Writes a new value to the address. Must be stable (`useCallback`).
 * @returns The text of the box and its setter.
 */
export function useAddressText(committed: string, commit: (text: string) => void): [string, (text: string) => void] {
  const [text, setText] = useState(committed);

  // `pending` is the value this hook has written and not yet seen arrive: its own write must not replace what the user
  // typed in the meantime. Any other change of `committed` came from outside.
  const [seen, setSeen] = useState(committed);
  const [pending, setPending] = useState<string | null>(null);
  if (committed !== seen) {
    setSeen(committed);
    setPending(null);
    if (committed !== pending) {
      setText(committed);
    }
  }

  useEffect(() => {
    const trimmed = text.trim();
    if (trimmed === committed) {
      return;
    }

    const timer = window.setTimeout(() => {
      setPending(trimmed);
      commit(trimmed);
    }, COMMIT_DELAY_MS);
    return () => window.clearTimeout(timer);
  }, [text, committed, commit]);

  return [text, setText];
}
