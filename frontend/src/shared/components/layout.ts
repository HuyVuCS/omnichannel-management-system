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

/** The `VI | EN` control. */
export const LANGUAGE_SWITCH_WIDTH = 96;

/** Primary action buttons in page headers ("Thêm người dùng" is the longest so far). */
export const PAGE_ACTION_WIDTH = 176;

/** Form dialogs (create user, change role, reset password). */
export const MODAL_WIDTH = 480;

export const FILTER_WIDTH = 180;
export const SEARCH_WIDTH = 280;

/** The one brand colour (AntD `token.colorPrimary`); status colours are used for states only. */
export const PRIMARY_COLOR = '#1677ff';
