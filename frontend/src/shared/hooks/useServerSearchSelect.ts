import { useCallback, useMemo, useState } from 'react';
import { useDebouncedValue } from './useDebouncedValue';

const SEARCH_DEBOUNCE_MS = 300;

/**
 * What the pickers with server-side search share (product picker, customer picker): nothing is asked until the picker
 * is first opened, the typed text reaches the request only after a pause (one request per pause, not per key), and the
 * options are not filtered again in the client - they already are the server's matches.
 *
 * The picker passes `search` to its list query (skipped while `activated` is false), spreads `selectProps` on the AntD
 * `Select` and calls `clearSearch` when a value is chosen.
 */
export function useServerSearchSelect() {
  const [activated, setActivated] = useState(false);
  const [searchText, setSearchText] = useState('');
  const search = useDebouncedValue(searchText.trim(), SEARCH_DEBOUNCE_MS);

  const clearSearch = useCallback(() => setSearchText(''), []);
  const selectProps = useMemo(
    () => ({
      showSearch: { filterOption: false, onSearch: setSearchText },
      onOpenChange: (open: boolean) => {
        if (open) {
          setActivated(true);
        }
      },
    }),
    [],
  );

  return { activated, search: search || undefined, selectProps, clearSearch };
}
