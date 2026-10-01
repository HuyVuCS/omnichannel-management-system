import { Select, type SelectProps } from 'antd';
import { useTranslation } from 'react-i18next';
import { ROLES, type Role } from '@/features/auth/session';

/** Role picker with translated labels; the values are the API's role names. */
export function RoleSelect(props: Omit<SelectProps<Role>, 'options'>) {
  const { t } = useTranslation();

  return <Select<Role> {...props} options={ROLES.map((role) => ({ value: role, label: t(`roles.${role}`) }))} />;
}
