import { Alert } from 'antd';

/** Form-level errors of a dialog (API errors that belong to no field); renders nothing when there are none. */
export function FormErrors({ errors }: { errors: string[] }) {
  if (errors.length === 0) {
    return null;
  }

  return <Alert type="error" showIcon title={errors.join(' ')} style={{ marginBottom: 16 }} />;
}
