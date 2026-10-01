import { skipToken } from '@reduxjs/toolkit/query';
import { Select, Spin, Typography } from 'antd';
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { useServerSearchSelect } from '@/shared/hooks/useServerSearchSelect';
import { formatNumber } from '@/shared/utils/format';
import { productLabel, useGetProductQuery, useListProductsQuery, type Product } from '../productsApi';

const PAGE_SIZE = 20;
const NO_OPTIONS: ProductOption[] = [];

interface ProductOption {
  value: string;
  label: string;
  /** Full text on hover: the label is cut when it is wider than the dropdown. */
  title: string;
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
 *
 * A label that does not fit (the dropdown is as wide as the picker) is cut with an ellipsis; the full text is the
 * `title` of the option and of the selected value, so hovering shows it.
 */
export function ProductSelect({ value, onChange, disabledIds, placeholder, ...rest }: ProductSelectProps) {
  const { t } = useTranslation();
  const { activated, search, searchPending, selectProps, clearSearch } = useServerSearchSelect();

  const list = useListProductsQuery({ page: 1, pageSize: PAGE_SIZE, search }, { skip: !activated });
  const listed = list.data?.items;
  const valueIsListed = value !== undefined && (listed?.some((product) => product.productId === value) ?? false);
  // The label of a value that is not among the current options: preselected from the address, or searched away.
  const selected = useGetProductQuery(value === undefined || valueIsListed ? skipToken : value);

  const options = useMemo<ProductOption[]>(
    () =>
      (listed ?? []).map((product) => ({
        value: product.productId,
        label: productLabel(product),
        title: productLabel(product),
        disabled: product.productId !== value && (disabledIds?.includes(product.productId) ?? false),
        product,
      })),
    [disabledIds, listed, value],
  );

  // The typed text is not what the options answer yet: the request has not been sent, or is still running.
  const searching = searchPending || list.isFetching;
  const selectedProduct = listed?.find((product) => product.productId === value) ?? selected.data;

  return (
    <Select<string, ProductOption>
      {...rest}
      {...selectProps}
      value={value}
      placeholder={placeholder ?? t('productSelect.placeholder')}
      // Options that answer an older search are not offered (Enter would choose the first of them).
      options={searching ? NO_OPTIONS : options}
      notFoundContent={searching ? <Spin size="small" /> : undefined}
      loading={list.isFetching || selected.isFetching}
      onChange={(productId: string | undefined, option) => {
        clearSearch();
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
