import { theme } from 'antd';
import type { CSSProperties } from 'react';

/**
 * The font size, line height and weight of an AntD heading level, for a heading of another level: every page has
 * exactly one `<h1>` (its title, task 027 A4) that keeps the look it had as a smaller heading.
 */
export function useHeadingLook(level: 3 | 4): CSSProperties {
  const { token } = theme.useToken();
  return level === 3
    ? { fontSize: token.fontSizeHeading3, lineHeight: token.lineHeightHeading3, fontWeight: token.fontWeightStrong }
    : { fontSize: token.fontSizeHeading4, lineHeight: token.lineHeightHeading4, fontWeight: token.fontWeightStrong };
}
