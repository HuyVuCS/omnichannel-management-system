import { Typography } from 'antd';

/**
 * One line of text that may be longer than its cell: truncated with an ellipsis, full text as a tooltip. Used wherever a
 * width is fixed (table cells, the header), so content never changes the layout.
 */
export function TruncatedText({ text }: { text: string | null | undefined }) {
  if (!text) {
    return null;
  }

  return (
    <Typography.Text ellipsis={{ tooltip: text }} style={{ maxWidth: '100%' }}>
      {text}
    </Typography.Text>
  );
}
