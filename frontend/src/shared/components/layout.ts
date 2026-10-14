/**
 * Fixed layout measurements. They are constants (not content-sized) so that switching the UI language changes text
 * only: every width below is designed for the longer of the two texts, and longer text is truncated with a tooltip.
 */
export const SIDER_WIDTH = 240;
export const SIDER_COLLAPSED_WIDTH = 64;
export const HEADER_HEIGHT = 56;

/** Header: e-mail + role of the signed-in user. */
export const USER_BLOCK_WIDTH = 320;
export const ROLE_TAG_WIDTH = 96;

/** Header: the state of the realtime connection (icon + "Đang kết nối lại" / "Reconnecting"). */
export const CONNECTION_INDICATOR_WIDTH = 148;

/** The `VI | EN` control. */
export const LANGUAGE_SWITCH_WIDTH = 96;

/** Primary action buttons in page headers ("Thêm người dùng" is the longest so far). */
export const PAGE_ACTION_WIDTH = 176;

/** A button with a sentence-like label ("Xem biến động của chứng từ này" / "View movements of this document"). */
export const WIDE_ACTION_WIDTH = 280;

/** Secondary buttons next to it ("Làm mới" / "Refresh", "Xoá bộ lọc" / "Clear filters"). */
export const SECONDARY_ACTION_WIDTH = 136;

/** Form dialogs. */
export const MODAL_WIDTH = 480;

/** Table column with one icon action per row, or two small ones (`size="small"` buttons in a dense table). */
export const SINGLE_ACTION_COLUMN_WIDTH = 88;

/** Table column with two icon actions per row. */
export const DOUBLE_ACTION_COLUMN_WIDTH = 124;

/** The page-size selector of a table's pagination. */
export const PAGE_SIZE_SELECT_WIDTH = 120;

export const FILTER_WIDTH = 180;

/**
 * A filter that takes several values: wide enough for two chosen values and "+ n" on one line in both languages, so
 * choosing more never makes the control taller.
 */
export const MULTI_FILTER_WIDTH = 360;

export const SEARCH_WIDTH = 280;

/** A text filter that takes an id: wide enough to show a whole GUID (36 characters) next to its clear button. */
export const ID_FILTER_WIDTH = 320;

/** A product picker (`SKU — name`) used as a filter or in a form line. */
export const PRODUCT_SELECT_WIDTH = 360;

/**
 * A quantity input of a form line. Wide enough for the API's message under it ("'Số lượng' phải có giá trị trong
 * khoảng từ 1 đến 1000000 ...").
 */
export const QUANTITY_INPUT_WIDTH = 240;

/** The label column of a summary (`Descriptions`): fixed, so the values start at the same place in both languages. */
export const SUMMARY_LABEL_WIDTH = 220;

/**
 * The same for a summary whose labels are all short (a processing task): the values get the room, so an id (a GUID
 * with its copy button) stays on one line in a two-column summary.
 */
export const SHORT_SUMMARY_LABEL_WIDTH = 160;

/** A date-range filter (two `DD/MM/YYYY` inputs). */
export const DATE_RANGE_WIDTH = 280;

/** Dashboard: the range presets ("Last 24 hours" is the longest of the four labels). */
export const DASHBOARD_PRESETS_WIDTH = 480;

/** Dashboard: "Cập nhật lúc 08:05:09" / "Updated at 08:05:09" next to the refresh button. */
export const DASHBOARD_UPDATED_WIDTH = 190;

/**
 * The one brand colour (AntD `token.colorPrimary`); status colours are used for states only. AntD's blue-7: as text
 * on white 6.2:1 and under white text the same (its default, blue-6 `#1677ff`, gives 4.1:1 - below the 4.5:1 of
 * WCAG AA, measured with axe in the browser in task 026c).
 */
export const PRIMARY_COLOR = '#0958d9';

/**
 * The colours of the theme that carry text, each at least 4.5:1 (WCAG AA) on white and on the tinted background AntD
 * derives from it (tags, alerts, the selected menu item). Measured in the browser with axe (report 026c); AntD's
 * defaults in brackets with what they gave.
 */
export const THEME_COLORS = {
  colorPrimary: PRIMARY_COLOR,
  /**
   * Hover of primary buttons (white text) and of primary-coloured text: AntD derives `#2e7ae6` (4.2:1 with white -
   * found by axe in the browser, task 027 C4); a slightly lighter blue than the brand colour keeps the hover visible
   * at 5.8:1.
   */
  colorPrimaryHover: '#1f5fd1',
  /** "Processing" tags and info alerts follow the brand colour. */
  colorInfo: PRIMARY_COLOR,
  /** green-8 (`#52c41a`: 2.2:1 as tag text). */
  colorSuccess: '#237804',
  /** (`#faad14`: below 2:1 as tag text.) */
  colorWarning: '#a35a00',
  /** red-7 (`#ff4d4f`: 3.3:1 - also the text of a field error). */
  colorError: '#cf1322',
  // The tints behind a status text stay AntD's light ones: derived from the darker colours above they would turn
  // grey and dark (seen in the browser: a success tag on #adb8a5).
  colorSuccessBg: '#f6ffed',
  colorSuccessBorder: '#b7eb8f',
  colorWarningBg: '#fffbe6',
  colorWarningBorder: '#ffe58f',
  colorErrorBg: '#fff2f0',
  colorErrorBorder: '#ffccc7',
  /** Secondary text, statistic titles, hints (45 % black: 3.3:1). */
  colorTextDescription: 'rgba(0, 0, 0, 0.65)',
  /** Labels of a description list (the same 45 % black). */
  colorTextTertiary: 'rgba(0, 0, 0, 0.65)',
  /** Placeholders - in the filters they are the visible name of the field (25 % black: 1.8:1). */
  colorTextPlaceholder: 'rgba(0, 0, 0, 0.6)',
} as const;
