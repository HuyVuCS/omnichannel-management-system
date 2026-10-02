export const CHANNELS_PATH = '/channels';

/** Route pattern of the channel detail page (the channel and the products mapped on it). */
export const CHANNEL_DETAIL_ROUTE = `${CHANNELS_PATH}/:channelId`;

/**
 * The API lets Manager and Admin create and change product mappings; everyone signed in may read them. Kept next to
 * the paths (not in channelsApi), like the roles of the stock import.
 */
export const MAPPING_WRITE_ROLES = ['Admin', 'Manager'] as const;

/** The detail page of one channel. */
export function channelPath(channelId: string): string {
  return `${CHANNELS_PATH}/${encodeURIComponent(channelId)}`;
}
