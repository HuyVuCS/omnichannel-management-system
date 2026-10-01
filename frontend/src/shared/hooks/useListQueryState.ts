import type { TablePaginationConfig } from 'antd';
import type { SorterResult, TableCurrentDataSource } from 'antd/es/table/interface';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { PAGE_SIZE_SELECT_WIDTH } from '@/shared/components/layout';

export type SortDirection = 'asc' | 'desc';

export const DEFAULT_PAGE_SIZE = 20;
export const PAGE_SIZES = [10, 20, 50];
const SEARCH_DEBOUNCE_MS = 300;

export interface ListQueryOptions<SortField extends string> {
  /** Sort fields the API accepts (its whitelist). Table columns use the same names as their `key`. */
  sortFields: readonly SortField[];
  /** The API's default sort field; `?sortDir=desc` without `sortBy` applies to it. */
  defaultSortField: SortField;
}

function positiveInt(value: string | null, fallback: number): number {
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : fallback;
}

/**
 * Paging, sorting and search of a server-side list, kept in the query string (`page`, `pageSize`, `search`, `sortBy`,
 * `sortDir`): the state survives reloads and language switches and can be shared as a link. Default values are left out
 * of the address. Page-specific filters use `searchParams` / `patchParams` with their own keys.
 *
 * The values returned here are what the API call needs; the helpers bind an AntD table and a search box to them.
 */
export function useListQueryState<SortField extends string>({
  sortFields,
  defaultSortField,
}: ListQueryOptions<SortField>) {
  const [searchParams, setSearchParams] = useSearchParams();

  const state = useMemo(() => {
    const pageSize = positiveInt(searchParams.get('pageSize'), DEFAULT_PAGE_SIZE);
    const rawSortDir = searchParams.get('sortDir');
    const sortDir: SortDirection | undefined = rawSortDir === 'asc' || rawSortDir === 'desc' ? rawSortDir : undefined;
    const rawSortBy = searchParams.get('sortBy');
    const sortField = (sortFields as readonly string[]).includes(rawSortBy ?? '')
      ? (rawSortBy as SortField)
      : undefined;

    return {
      page: positiveInt(searchParams.get('page'), 1),
      pageSize: PAGE_SIZES.includes(pageSize) ? pageSize : DEFAULT_PAGE_SIZE,
      search: searchParams.get('search')?.trim() || undefined,
      // An explicit sort is a field plus a direction; without a direction the API's default order applies.
      sortBy: sortDir ? (sortField ?? defaultSortField) : undefined,
      sortDir,
    };
  }, [searchParams, sortFields, defaultSortField]);

  const patchParams = useCallback(
    (changes: Record<string, string | number | undefined>) => {
      setSearchParams(
        (previous) => {
          const next = new URLSearchParams(previous);
          for (const [key, value] of Object.entries(changes)) {
            if (value === undefined || value === '') {
              next.delete(key);
            } else {
              next.set(key, String(value));
            }
          }

          return next;
        },
        { replace: true },
      );
    },
    [setSearchParams],
  );

  // Search box: typed text is local; the query string (and with it the request) follows after a pause.
  const addressSearch = state.search ?? '';
  const [searchText, setSearchText] = useState(addressSearch);

  // The other direction: when `search` in the address changes and this hook did not write that value itself (a menu
  // link to the same page, back/forward, an edited address), the box follows the address. `pendingSearch` is the value
  // this hook has written and not yet seen arrive, so its own write never overwrites what was typed meanwhile.
  const [seenSearch, setSeenSearch] = useState(addressSearch);
  const [pendingSearch, setPendingSearch] = useState<string | null>(null);
  if (addressSearch !== seenSearch) {
    setSeenSearch(addressSearch);
    setPendingSearch(null);
    if (addressSearch !== pendingSearch) {
      setSearchText(addressSearch);
    }
  }

  useEffect(() => {
    const text = searchText.trim();
    if (text === addressSearch) {
      return;
    }

    const timer = window.setTimeout(() => {
      setPendingSearch(text);
      // A new search term starts on the first page.
      patchParams({ search: text, page: undefined });
    }, SEARCH_DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [searchText, addressSearch, patchParams]);

  /** `onChange` of the AntD table: paging and sorting go to the query string. */
  const onTableChange = useCallback(
    <Row>(
      pagination: TablePaginationConfig,
      _filters: unknown,
      sorter: SorterResult<Row> | SorterResult<Row>[],
      extra: TableCurrentDataSource<Row>,
    ) => {
      const sort = Array.isArray(sorter) ? sorter[0] : sorter;
      const sortDir: SortDirection | undefined =
        sort?.order === 'descend' ? 'desc' : sort?.order === 'ascend' ? 'asc' : undefined;
      const sortBy = sortDir ? String(sort?.columnKey ?? defaultSortField) : undefined;

      patchParams({
        // A new sort order starts on the first page.
        page: extra.action === 'paginate' && pagination.current !== 1 ? pagination.current : undefined,
        pageSize: pagination.pageSize === DEFAULT_PAGE_SIZE ? undefined : pagination.pageSize,
        sortBy: sortBy === defaultSortField ? undefined : sortBy,
        sortDir,
      });
    },
    [defaultSortField, patchParams],
  );

  /** `pagination` of the AntD table. */
  const pagination = useCallback(
    (totalCount: number | undefined): TablePaginationConfig => ({
      current: state.page,
      pageSize: state.pageSize,
      total: totalCount ?? 0,
      pageSizeOptions: PAGE_SIZES,
      // Fixed width: the text ("20 / trang", "20 / page") differs per language, the control must not.
      showSizeChanger: { style: { width: PAGE_SIZE_SELECT_WIDTH } },
    }),
    [state.page, state.pageSize],
  );

  /** `sortOrder` of a sortable column. */
  const sortOrder = useCallback(
    (field: SortField): 'ascend' | 'descend' | null =>
      state.sortBy === field ? (state.sortDir === 'desc' ? 'descend' : 'ascend') : null,
    [state.sortBy, state.sortDir],
  );

  return { ...state, searchParams, patchParams, searchText, setSearchText, onTableChange, pagination, sortOrder };
}
