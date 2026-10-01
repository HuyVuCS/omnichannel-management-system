import { Typography } from 'antd';

interface FieldHintProps {
  text: string;
  /** Lines reserved for the hint. The height is always exactly this many lines. */
  rows?: 1 | 2;
}

/** Line height of AntD's default 14px text. */
const LINE_HEIGHT = 22;

/**
 * Help text under a form field (`Form.Item extra`) with a fixed height: the text may wrap differently per language, so
 * the space is reserved up front and longer text is truncated with a tooltip. The form never changes size when the
 * language changes.
 */
export function FieldHint({ text, rows = 1 }: FieldHintProps) {
  return (
    <Typography.Paragraph
      type="secondary"
      ellipsis={{ rows, tooltip: text }}
      style={{ height: rows * LINE_HEIGHT, lineHeight: `${LINE_HEIGHT}px`, margin: 0 }}
    >
      {text}
    </Typography.Paragraph>
  );
}
