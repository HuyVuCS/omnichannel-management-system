import { baseApi } from '@/shared/api/baseApi';

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

const LIST_TAG = { type: 'Channel', id: 'LIST' } as const;

/** Invalidate the list only when the mutation succeeded: a rejected one changed nothing on the server. */
const listOnSuccess = (_result: unknown, error: unknown) => (error ? [] : [LIST_TAG]);

export const channelsApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    /** All channels (a handful; the endpoint is not paged). */
    listChannels: build.query<Channel[], void>({
      query: () => 'channels',
      providesTags: [LIST_TAG],
    }),
    createChannel: build.mutation<Channel, CreateChannelRequest>({
      query: (body) => ({ url: 'channels', method: 'POST', body }),
      invalidatesTags: listOnSuccess,
    }),
    updateChannel: build.mutation<Channel, { channelId: string; changes: UpdateChannelRequest }>({
      query: ({ channelId, changes }) => ({ url: `channels/${channelId}`, method: 'PUT', body: changes }),
      invalidatesTags: listOnSuccess,
    }),
  }),
});

export const { useListChannelsQuery, useCreateChannelMutation, useUpdateChannelMutation } = channelsApi;
