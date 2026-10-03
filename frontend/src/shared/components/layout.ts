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

/** The one brand colour (AntD `token.colorPrimary`); status colours are used for states only. */
export const PRIMARY_COLOR = '#1677ff';
