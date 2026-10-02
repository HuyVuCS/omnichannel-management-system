import { skipToken } from '@reduxjs/toolkit/query';
import { Select, Spin, Typography } from 'antd';
import { useMemo, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { optionsAreStale, useServerSearchSelect } from '@/shared/hooks/useServerSearchSelect';
import { customerContact, useGetCustomerQuery, useListCustomersQuery, type Customer } from '../customersApi';

const PAGE_SIZE = 20;
const NO_OPTIONS: CustomerOption[] = [];

interface CustomerOption {
  value: string;
  label: string;
  /** Full text on hover (name and contact): both are cut when they do not fit into the dropdown. */
  title: string;
  customer: Customer;
}

interface CustomerSelectProps {
  /** Customer id. */
  value?: string;
  /** Second argument: the chosen customer (undefined when cleared). */
  onChange?: (customerId: string | undefined, customer?: Customer) => void;
  allowClear?: boolean;
  placeholder?: string;
  style?: CSSProperties;
  id?: string;
  'aria-label'?: string;
}

/** Name and contact of a customer as one text (the option's `title`). */
function fullText(customer: Customer): string {
  const contact = customerContact(customer);
  return contact ? `${customer.name} — ${contact}` : customer.name;
}

/**
 * Customer picker with server-side search (`GET /api/customers?search=…&pageSize=20`, debounced), the counterpart of
 * the product picker: options show the name with the phone number or e-mail address as secondary text, nothing is
 * loaded until the picker is first opened, and a value set from outside (an id in the address) gets its label from
 * `GET /api/customers/{id}`. Text that does not fit is cut with an ellipsis; the full text is the `title` of the option
 * and of the selected value.
 */
export function CustomerSelect({ value, onChange, placeholder, ...rest }: CustomerSelectProps) {
  const { t } = useTranslation();
  const { activated, search, searchPending, selectProps, clearSearch } = useServerSearchSelect();

  const list = useListCustomersQuery({ page: 1, pageSize: PAGE_SIZE, search }, { skip: !activated });
  const listed = list.data?.items;
  const valueIsListed = value !== undefined && (listed?.some((customer) => customer.customerId === value) ?? false);
  // The label of a value that is not among the current options: preselected from the address, or searched away.
  const selected = useGetCustomerQuery(value === undefined || valueIsListed ? skipToken : value);

  const options = useMemo<CustomerOption[]>(
    () =>
      (listed ?? []).map((customer) => ({
        value: customer.customerId,
        label: customer.name,
        title: fullText(customer),
        customer,
      })),
    [listed],
  );

  // The typed text is not what the options answer yet: the request has not been sent, or is still running. A refetch
  // of the same search keeps its options.
  const searching = optionsAreStale(searchPending, list);
  const selectedCustomer = listed?.find((customer) => customer.customerId === value) ?? selected.data;

  return (
    <Select<string, CustomerOption>
      {...rest}
      {...selectProps}
      value={value}
      placeholder={placeholder ?? t('customerSelect.placeholder')}
      // Options that answer an older search are not offered (Enter would choose the first of them).
      options={searching ? NO_OPTIONS : options}
      notFoundContent={searching ? <Spin size="small" /> : undefined}
      loading={list.isFetching || selected.isFetching}
      onChange={(customerId: string | undefined, option) => {
        clearSearch();
        onChange?.(customerId, Array.isArray(option) ? undefined : option?.customer);
      }}
      labelRender={({ value: selectedValue }) =>
        selectedCustomer && selectedCustomer.customerId === selectedValue ? selectedCustomer.name : ''
      }
      optionRender={(option) => {
        const contact = customerContact(option.data.customer);
        return (
          <div style={{ display: 'flex', gap: 8 }}>
            <Typography.Text ellipsis style={{ flex: '1 1 auto', minWidth: 0 }}>
              {option.data.label}
            </Typography.Text>
            {contact && (
              // The contact may shrink too (a long e-mail address), but never takes more than half of the option.
              <Typography.Text type="secondary" ellipsis style={{ flex: '0 1 auto', minWidth: 0, maxWidth: '50%' }}>
                {contact}
              </Typography.Text>
            )}
          </div>
        );
      }}
    />
  );
}
