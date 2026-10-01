import { Tag } from 'antd';
import { useTranslation } from 'react-i18next';

/**
 * "Low stock" / "In stock". The rule is the server's (`isLowStock` on product and inventory responses); it is only
 * displayed here, with the same tag on every page.
 */
export function StockStatusTag({ isLowStock }: { isLowStock: boolean }) {
  const { t } = useTranslation();

  return isLowStock ? (
    <Tag color="warning">{t('products.stock.low')}</Tag>
  ) : (
    <Tag color="success">{t('products.stock.ok')}</Tag>
  );
}
