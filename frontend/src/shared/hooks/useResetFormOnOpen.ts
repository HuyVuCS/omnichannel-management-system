import type { FormInstance } from 'antd';
import { useEffect } from 'react';

/**
 * Resets a dialog's form to its current `initialValues` every time the dialog opens.
 *
 * `destroyOnHidden` alone is not enough: the form is only destroyed when the close animation has finished. A dialog
 * reopened before that (a quick second click, a slow machine) would still hold the previous values — for an edit
 * dialog that means the values of another record under the new record's id.
 *
 * @param opening What the dialog is open for (the edited record, a state object, or `true`); falsy while closed. The
 *                form is reset whenever this changes to a truthy value.
 */
export function useResetFormOnOpen<Values>(form: FormInstance<Values>, opening: unknown): void {
  useEffect(() => {
    if (opening) {
      form.resetFields();
    }
  }, [form, opening]);
}
