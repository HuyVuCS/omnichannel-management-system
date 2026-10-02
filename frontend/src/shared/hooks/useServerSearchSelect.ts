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
 *
 * While `searchPending` is true (text was typed that the request does not carry yet) - and while that request is
 * running - the options on screen answer an older search. The picker must not offer them: Enter would choose the
 * first of them, i.e. something the user did not search for (found in the browser: typing a SKU and pressing Enter
 * at once put a different product on the line). The picker asks `optionsAreStale` with its list query.
 */
export function useServerSearchSelect() {
  const [activated, setActivated] = useState(false);
  const [searchText, setSearchText] = useState('');
  const typed = searchText.trim();
  const search = useDebouncedValue(typed, SEARCH_DEBOUNCE_MS);

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

  return { activated, search: search || undefined, searchPending: typed !== search, selectProps, clearSearch };
}

/**
 * True while the options of a picker's list query do not answer what was typed: the typed text has not reached the
 * request yet, or the request for it is still running. RTK Query's `currentData` is `undefined` exactly while a
 * *different* argument is loading; a background refetch of the *same* search (an invalidation while the dropdown is
 * open) keeps it, and with it the options.
 */
export function optionsAreStale(searchPending: boolean, list: { isFetching: boolean; currentData?: unknown }): boolean {
  return searchPending || (list.isFetching && list.currentData === undefined);
}
