import { skipToken } from '@reduxjs/toolkit/query';
import { Select, Typography } from 'antd';
import { useMemo, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useDebouncedValue } from '@/shared/hooks/useDebouncedValue';
import { formatNumber } from '@/shared/utils/format';
import { productLabel, useGetProductQuery, useListProductsQuery, type Product } from '../productsApi';

const SEARCH_DEBOUNCE_MS = 300;
const PAGE_SIZE = 20;

interface ProductOption {
  value: string;
  label: string;
  disabled: boolean;
  product: Product;
}

interface ProductSelectProps {
  /** Product id. */
  value?: string;
  /** Second argument: the chosen product (undefined when cleared). */
  onChange?: (productId: string | undefined, product?: Product) => void;
  /** Products that cannot be chosen here (e.g. already on another line). Convenience only; the API decides. */
  disabledIds?: readonly string[];
  allowClear?: boolean;
  placeholder?: string;
  style?: CSSProperties;
  id?: string;
  'aria-label'?: string;
}

/**
 * Product picker with server-side search (`GET /api/products?search=…&pageSize=20`, debounced). Options show
 * `SKU — name` and the available stock. Nothing is cached in the client beyond the API cache: the options are one page
 * of the product list, loaded when the picker is first opened, and a value set from outside (an id in the address)
 * gets its label from `GET /api/products/{id}`.
 */
export function ProductSelect({ value, onChange, disabledIds, placeholder, ...rest }: ProductSelectProps) {
  const { t } = useTranslation();
  const [activated, setActivated] = useState(false);
  const [searchText, setSearchText] = useState('');
  const search = useDebouncedValue(searchText.trim(), SEARCH_DEBOUNCE_MS);

  const list = useListProductsQuery(
    { page: 1, pageSize: PAGE_SIZE, search: search || undefined },
    { skip: !activated },
  );
  const listed = list.data?.items;
  const valueIsListed = value !== undefined && (listed?.some((product) => product.productId === value) ?? false);
  // The label of a value that is not among the current options: preselected from the address, or searched away.
  const selected = useGetProductQuery(value === undefined || valueIsListed ? skipToken : value);

  const options = useMemo<ProductOption[]>(
    () =>
      (listed ?? []).map((product) => ({
        value: product.productId,
        label: productLabel(product),
        disabled: product.productId !== value && (disabledIds?.includes(product.productId) ?? false),
        product,
      })),
    [disabledIds, listed, value],
  );

  const selectedProduct = listed?.find((product) => product.productId === value) ?? selected.data;

  return (
    <Select<string, ProductOption>
      {...rest}
      value={value}
      placeholder={placeholder ?? t('productSelect.placeholder')}
      options={options}
      loading={list.isFetching || selected.isFetching}
      // The server searches; the options are already the matches.
      showSearch={{ filterOption: false, onSearch: setSearchText }}
      onOpenChange={(open) => {
        if (open) {
          setActivated(true);
        }
      }}
      onChange={(productId: string | undefined, option) => {
        setSearchText('');
        onChange?.(productId, Array.isArray(option) ? undefined : option?.product);
      }}
      labelRender={({ value: selectedValue }) =>
        selectedProduct && selectedProduct.productId === selectedValue ? productLabel(selectedProduct) : ''
      }
      optionRender={(option) => (
        <div style={{ display: 'flex', gap: 8 }}>
          <Typography.Text ellipsis disabled={option.data.disabled} style={{ flex: 1, minWidth: 0 }}>
            {option.data.label}
          </Typography.Text>
          <Typography.Text type="secondary" style={{ flex: 'none' }}>
            {t('productSelect.available', { stock: formatNumber(option.data.product.availableStock) })}
          </Typography.Text>
        </div>
      )}
    />
  );
}
