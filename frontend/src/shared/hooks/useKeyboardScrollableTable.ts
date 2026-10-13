import type { TableRef } from 'antd/es/table';
import { useEffect, useRef } from 'react';

/**
 * A table that scrolls sideways (`scroll.x`) and has nothing focusable in its rows cannot be scrolled with the
 * keyboard: there is nothing to tab to. Its scroll container becomes a tab stop — a named region, scrolled with the
 * arrow keys while it has the focus (axe `scrollable-region-focusable`, found in the browser in task 026c).
 *
 * Pass the returned ref to the `<Table>`. A table whose rows hold links or buttons does not need it.
 */
export function useKeyboardScrollableTable(label: string) {
  const ref = useRef<TableRef>(null);

  // After every render: the table builds its scroll container itself and may build it again.
  useEffect(() => {
    const region = ref.current?.nativeElement.querySelector<HTMLElement>('.ant-table-content');
    if (region) {
      region.tabIndex = 0;
      region.setAttribute('role', 'region');
      region.setAttribute('aria-label', label);
    }
  });

  return ref;
}
