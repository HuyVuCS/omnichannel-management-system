import { baseApi } from '@/shared/api/baseApi';
import { isApiError, type PagedResult } from '@/shared/api/types';
import type { SortDirection } from '@/shared/hooks/useListQueryState';

/** Values of the API's `ChannelType` and `ChannelStatus` enums (serialised as strings). */
export const CHANNEL_TYPES = ['Website', 'Pos', 'DigitalPlatform'] as const;
export type ChannelType = (typeof CHANNEL_TYPES)[number];

export const CHANNEL_STATUSES = ['Active', 'Inactive'] as const;
export type ChannelStatus = (typeof CHANNEL_STATUSES)[number];

export interface Channel {
  channelId: string;
  name: string;
  /** Orders and webhooks are routed by slug (`POST /api/orders/{slug}`); it never changes. */
  slug: string;
  type: ChannelType;
  status: ChannelStatus;
  createdAt: string;
  updatedAt: string | null;
  /**
   * The server's answer to "is stock pushed to this channel at all": a capability of the channel, independent of its
   * status. The client only displays it.
   */
  stockSyncEnabled: boolean;
}

export interface CreateChannelRequest {
  name: string;
  slug: string;
  type: ChannelType;
}

/** Slug and type are immutable. */
export interface UpdateChannelRequest {
  name: string;
  status: ChannelStatus;
}

/** Values of the API's `ChannelProductStatus` enum. */
export const CHANNEL_PRODUCT_STATUSES = ['Active', 'Inactive'] as const;
export type ChannelProductStatus = (typeof CHANNEL_PRODUCT_STATUSES)[number];

/** A product mapped on a channel: the central product and how the channel identifies it. */
export interface ChannelProduct {
  channelProductId: string;
  channelId: string;
  productId: string;
  sku: string;
  productName: string;
  externalProductId: string | null;
  externalSku: string | null;
  status: ChannelProductStatus;
  createdAt: string;
  updatedAt: string | null;
}

/** Answer of a created or changed mapping. */
export interface SavedChannelProduct extends ChannelProduct {
  /**
   * The stock-sync task the request started, or null when it started none. Why none is the server's knowledge (channel
   * or mapping inactive, channel without stock sync, nothing changed that the channel has to learn).
   */
  syncTaskId: string | null;
}

/** Sort fields of `GET /api/channels/{channelId}/products` (default: `sku`). */
export const CHANNEL_PRODUCT_SORT_FIELDS = ['sku', 'createdAt'] as const;
export type ChannelProductSortField = (typeof CHANNEL_PRODUCT_SORT_FIELDS)[number];

/** `search` matches SKU, product name, external product id and external SKU. */
export interface ChannelProductListArgs {
  channelId: string;
  page: number;
  pageSize: number;
  search?: string;
  sortBy?: ChannelProductSortField;
  sortDir?: SortDirection;
}

/** The external ids are sent as typed (the server trims them and requires one of the two); an empty one as null. */
export interface CreateChannelProductRequest {
  productId: string;
  externalProductId: string | null;
  externalSku: string | null;
}

/** Product and channel of a mapping cannot change; there is no delete (deactivate instead). */
export interface UpdateChannelProductRequest {
  externalProductId: string | null;
  externalSku: string | null;
  status: ChannelProductStatus;
}

const LIST_TAG = { type: 'Channel', id: 'LIST' } as const;

/** Every mapping list. No realtime hint names mappings: only users change them. */
export const CHANNEL_PRODUCT_LIST_TAG = { type: 'ChannelProduct', id: 'LIST' } as const;

/** Invalidate the list only when the mutation succeeded: a rejected one changed nothing on the server. */
const listOnSuccess = (_result: unknown, error: unknown) => (error ? [] : [LIST_TAG]);

/** The API says the mapping does not exist (any more): the list on screen still shows it. */
const mappingIsGone = (error: unknown) => isApiError(error) && error.problem?.errorCode === 'channel_product_not_found';

function compact(params: Record<string, string | number | undefined>) {
  return Object.fromEntries(Object.entries(params).filter(([, value]) => value !== undefined && value !== ''));
}

export const channelsApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    /** All channels (a handful; the endpoint is not paged). */
    listChannels: build.query<Channel[], void>({
      query: () => 'channels',
      providesTags: [LIST_TAG],
    }),
    /** One channel, for its detail page. */
    getChannel: build.query<Channel, string>({
      query: (channelId) => `channels/${channelId}`,
      providesTags: (_result, _error, channelId) => [{ type: 'Channel', id: channelId }],
    }),
    createChannel: build.mutation<Channel, CreateChannelRequest>({
      query: (body) => ({ url: 'channels', method: 'POST', body }),
      invalidatesTags: listOnSuccess,
    }),
    updateChannel: build.mutation<Channel, { channelId: string; changes: UpdateChannelRequest }>({
      query: ({ channelId, changes }) => ({ url: `channels/${channelId}`, method: 'PUT', body: changes }),
      invalidatesTags: (result, error, { channelId }) => [
        ...listOnSuccess(result, error),
        // The alert lists show the channel's name.
        ...(error
          ? []
          : [
              { type: 'Channel' as const, id: channelId },
              { type: 'Alert' as const, id: 'LIST' },
            ]),
      ],
    }),
    /** The products mapped on a channel (paged; `sku` ascending unless a sort is given). */
    listChannelProducts: build.query<PagedResult<ChannelProduct>, ChannelProductListArgs>({
      query: ({ channelId, ...params }) => ({ url: `channels/${channelId}/products`, params: compact(params) }),
      providesTags: [CHANNEL_PRODUCT_LIST_TAG],
    }),
    createChannelProduct: build.mutation<
      SavedChannelProduct,
      { channelId: string; mapping: CreateChannelProductRequest }
    >({
      query: ({ channelId, mapping }) => ({ url: `channels/${channelId}/products`, method: 'POST', body: mapping }),
      invalidatesTags: (_result, error) => (error ? [] : [CHANNEL_PRODUCT_LIST_TAG]),
    }),
    /**
     * A rejected change leaves the lists alone - except when the API says the mapping is gone: then the list that
     * offered it is stale.
     */
    updateChannelProduct: build.mutation<
      SavedChannelProduct,
      { channelId: string; channelProductId: string; changes: UpdateChannelProductRequest }
    >({
      query: ({ channelId, channelProductId, changes }) => ({
        url: `channels/${channelId}/products/${channelProductId}`,
        method: 'PUT',
        body: changes,
      }),
      invalidatesTags: (_result, error) => (!error || mappingIsGone(error) ? [CHANNEL_PRODUCT_LIST_TAG] : []),
    }),
  }),
});

export const {
  useListChannelsQuery,
  useGetChannelQuery,
  useCreateChannelMutation,
  useUpdateChannelMutation,
  useListChannelProductsQuery,
  useCreateChannelProductMutation,
  useUpdateChannelProductMutation,
} = channelsApi;
