/** One order-entry component of the page (architecture doc §3.1): the channel it sends to and how its order ids start. */
export interface OrderEntryPanelConfig {
  /** Channel slug: orders go to `POST /api/orders/{slug}`. */
  slug: string;
  /** Prefix of generated external order ids. */
  idPrefix: string;
  /** Translation key of the component's name. */
  titleKey: string;
}

/** The three order-entry components of the architecture, one per seeded channel slug, in page order. */
export const ORDER_ENTRY_PANELS: readonly OrderEntryPanelConfig[] = [
  { slug: 'digital-platform', idPrefix: 'DP', titleKey: 'orderEntry.panels.digitalPlatform' },
  { slug: 'website', idPrefix: 'WEB', titleKey: 'orderEntry.panels.website' },
  { slug: 'pos', idPrefix: 'POS', titleKey: 'orderEntry.panels.pos' },
];
