import { baseApi } from '@/shared/api/baseApi';
import type { PagedResult } from '@/shared/api/types';
import type { SortDirection } from '@/shared/hooks/useListQueryState';

export interface Customer {
  customerId: string;
  name: string;
  phone: string | null;
  email: string | null;
  address: string | null;
  createdAt: string;
  updatedAt: string | null;
}

/** Sort fields of `GET /api/customers` (default: `name`). */
export const CUSTOMER_SORT_FIELDS = ['name', 'createdAt'] as const;
export type CustomerSortField = (typeof CUSTOMER_SORT_FIELDS)[number];

export interface CustomerListArgs {
  page: number;
  pageSize: number;
  search?: string;
  sortBy?: CustomerSortField;
  sortDir?: SortDirection;
}

/** Body of both POST and PUT. */
export interface CustomerRequest {
  name: string;
  phone: string | null;
  email: string | null;
  address: string | null;
}

const LIST_TAG = { type: 'Customer', id: 'LIST' } as const;

/** Invalidate the list only when the mutation succeeded: a rejected one changed nothing on the server. */
const listOnSuccess = (_result: unknown, error: unknown) => (error ? [] : [LIST_TAG]);

function compact(params: Record<string, string | number | undefined>) {
  return Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined && value !== ''));
}

export const customersApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    listCustomers: build.query<PagedResult<Customer>, CustomerListArgs>({
      query: (args) => ({ url: 'customers', params: compact({ ...args }) }),
      providesTags: [LIST_TAG],
    }),
    createCustomer: build.mutation<Customer, CustomerRequest>({
      query: (body) => ({ url: 'customers', method: 'POST', body }),
      invalidatesTags: listOnSuccess,
    }),
    updateCustomer: build.mutation<Customer, { customerId: string; changes: CustomerRequest }>({
      query: ({ customerId, changes }) => ({ url: `customers/${customerId}`, method: 'PUT', body: changes }),
      invalidatesTags: listOnSuccess,
    }),
  }),
});

export const { useListCustomersQuery, useCreateCustomerMutation, useUpdateCustomerMutation } = customersApi;
