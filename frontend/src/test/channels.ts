import { http, HttpResponse } from 'msw';
import type { Channel, ChannelProduct, SavedChannelProduct } from '@/features/channels/channelsApi';
import type { PagedResult } from '@/shared/api/types';
import { COFFEE_ID, TEA_ID } from './catalog';
import { WEBSITE_ID } from './dashboard';
import { channel } from './orders';
import { api, problem, server } from './server';

/** A channel that was created through the API: it has no stock-sync queue. */
export const SHOP_ID = '0199a1b2-0000-7000-8000-00000000c00a';
export const TEA_MAPPING_ID = '0199a1b2-0000-7000-8000-0000000000d1';
export const COFFEE_MAPPING_ID = '0199a1b2-0000-7000-8000-0000000000d2';

/** The seeded website channel, with a GUID id (detail pages check ids taken from the address). */
export const websiteChannel = channel({ channelId: WEBSITE_ID, slug: 'website', name: 'Website', type: 'Website' });

/** A channel without stock sync. */
export const shopChannel = channel({
  channelId: SHOP_ID,
  slug: 'my-new-shop',
  name: 'Cửa hàng mới',
  type: 'Pos',
  stockSyncEnabled: false,
});

/** A row of `GET /api/channels/{channelId}/products`. */
export function mapping(
  overrides: Partial<ChannelProduct> & Pick<ChannelProduct, 'channelProductId' | 'productId' | 'sku' | 'productName'>,
): ChannelProduct {
  return {
    channelId: WEBSITE_ID,
    externalProductId: null,
    externalSku: null,
    status: 'Active',
    createdAt: new Date(2026, 9, 2, 9, 30).toISOString(),
    updatedAt: null,
    ...overrides,
  };
}

/** Tea is mapped by both external ids and was changed once; coffee by its external product id only, and is inactive. */
export const teaMapping = mapping({
  channelProductId: TEA_MAPPING_ID,
  productId: TEA_ID,
  sku: 'TEA-01',
  productName: 'Trà xanh',
  externalProductId: 'WEB-1001',
  externalSku: 'WEB-TEA',
  updatedAt: new Date(2026, 9, 2, 10, 45).toISOString(),
});
export const coffeeMapping = mapping({
  channelProductId: COFFEE_MAPPING_ID,
  productId: COFFEE_ID,
  sku: 'COF-01',
  productName: 'Cà phê',
  externalProductId: 'WEB-1002',
  status: 'Inactive',
  createdAt: new Date(2026, 9, 1, 8, 5).toISOString(),
});

/** What the API answers to a saved mapping. */
export function savedMapping(saved: ChannelProduct, syncTaskId: string | null): SavedChannelProduct {
  return { ...saved, syncTaskId };
}

interface ChannelBackend {
  /** What `GET /api/channels/{id}` returns; null = 404. */
  channel: Channel | null;
  items: ChannelProduct[];
  totalCount: number;
  /** Answer of `POST /api/channels/{id}/products`. */
  createAnswer: (body: unknown) => Response;
  /** Answer of `PUT /api/channels/{id}/products/{channelProductId}`. */
  updateAnswer: (channelProductId: string, body: unknown) => Response;
}

/**
 * The endpoints of the channel detail page: the channel, its mapping list (records the query string of every request)
 * and the create / change of a mapping (records the bodies). The answers can be replaced by a test.
 */
export function serveChannelDetail(shown: Channel | null, initial: ChannelProduct[] = [], totalCount = initial.length) {
  const state: ChannelBackend = {
    channel: shown,
    items: initial,
    totalCount,
    createAnswer: () => problem(500),
    updateAnswer: () => problem(500),
  };
  const channelReads: string[] = [];
  const listRequests: Record<string, string>[] = [];
  const creates: { channelId: string; body: unknown }[] = [];
  const updates: { channelProductId: string; body: unknown }[] = [];

  server.use(
    http.get(api('/channels/:id'), ({ params }) => {
      channelReads.push(String(params.id));
      return state.channel ? HttpResponse.json(state.channel) : problem(404, { errorCode: 'not_found' });
    }),
    http.get(api('/channels/:id/products'), ({ request }) => {
      const url = new URL(request.url);
      listRequests.push(Object.fromEntries(url.searchParams));
      const page: PagedResult<ChannelProduct> = {
        items: state.items,
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 20),
        totalCount: state.totalCount,
      };
      return HttpResponse.json(page);
    }),
    http.post(api('/channels/:id/products'), async ({ request, params }) => {
      const body = await request.json();
      creates.push({ channelId: String(params.id), body });
      return state.createAnswer(body);
    }),
    http.put(api('/channels/:id/products/:channelProductId'), async ({ request, params }) => {
      const body = await request.json();
      updates.push({ channelProductId: String(params.channelProductId), body });
      return state.updateAnswer(String(params.channelProductId), body);
    }),
  );

  return { state, channelReads, listRequests, creates, updates };
}
