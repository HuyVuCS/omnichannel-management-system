import { InputNumber, type InputNumberProps } from 'antd';

/** `1234567` → `1.234.567` (Vietnamese grouping, the same in both UI languages). */
function groupDigits(value: number | string | undefined): string {
  if (value === undefined || value === '') {
    return '';
  }

  return String(value).replace(/\B(?=(\d{3})+(?!\d))/g, '.');
}

function ungroupDigits(text: string | undefined): number {
  return Number((text ?? '').replace(/\./g, ''));
}

/**
 * Amount in VND: whole numbers, grouped while typing, never negative. Display only — the limits that matter are
 * validated by the API.
 */
export function MoneyInput(props: Omit<InputNumberProps<number>, 'formatter' | 'parser' | 'precision'>) {
  return (
    <InputNumber<number>
      min={0}
      controls={false}
      suffix="₫"
      {...props}
      precision={0}
      formatter={groupDigits}
      parser={ungroupDigits}
      style={{ width: '100%', ...props.style }}
    />
  );
}
